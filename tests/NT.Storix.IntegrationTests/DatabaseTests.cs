using Microsoft.Data.SqlClient;
using MongoDB.Bson;
using MongoDB.Driver;
using NT.Storix.Core.Engine;
using NT.Storix.Core.Models;
using NT.Storix.Core.Sources;
using Testcontainers.MongoDb;
using Testcontainers.MsSql;

namespace NT.Storix.IntegrationTests;

public class DatabaseTests
{
    [DockerFact]
    public async Task SqlServer_backup_restore_as_new_database_has_identical_data()
    {
        using var harness = new Harness();
        var shared = harness.Dir("shared");

        // The engine writes .bak files into a folder shared with the host (same path on both sides).
        await using var container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
            .WithBindMount(shared, shared)
            .WithCreateParameterModifier(p => p.User = "root")
            .Build();
        await container.StartAsync();

        var connectionString = container.GetConnectionString();
        await ExecuteAsync(connectionString, "CREATE DATABASE StorixIt");
        var dbConnection = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "StorixIt" }.ConnectionString;
        await ExecuteAsync(dbConnection, """
            CREATE TABLE Items (Id int PRIMARY KEY, Name nvarchar(100), Payload varbinary(max));
            INSERT INTO Items (Id, Name, Payload)
            SELECT TOP (2000) ROW_NUMBER() OVER (ORDER BY (SELECT 1)), CONCAT(N'item-', NEWID()), CRYPT_GEN_RANDOM(512)
            FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            """);

        var job = new BackupJob
        {
            Name = "SQL",
            Source = { Kind = SourceKind.SqlServer, SqlServer = { ConnectionString = connectionString, Databases = ["StorixIt"], BackupDirectory = shared, VerifyBackup = true } },
            Processing = { Encrypt = true, EncryptionPassword = "pw" },
            Destinations = [new DestinationDefinition { Name = "Local", LocalFolder = { Path = harness.Dir("target") } }],
        };
        var run = await harness.BackupAsync(job);
        Assert.Contains("RESTORE VERIFYONLY", run.Log);
        Assert.Empty(Directory.GetFiles(shared, "*.bak")); // Temporary .bak removed after the run.

        var restored = Path.Combine(shared, "restored");
        await RestoreService.RestoreFromFileAsync(Path.Combine(job.Destinations[0].LocalFolder.Path, run.FileName!), new RestoreRequest(restored, "pw"), null, CancellationToken.None);
        var bak = Directory.GetFiles(restored, "*.bak", SearchOption.AllDirectories).Single();

        await SqlServerRestorer.RestoreAsync(connectionString, bak, "StorixItRestored", null, replace: false, CancellationToken.None);

        var original = await FingerprintAsync(dbConnection);
        var copy = await FingerprintAsync(new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "StorixItRestored" }.ConnectionString);
        Assert.Equal(original, copy);
    }

    [DockerFact]
    public async Task MongoDb_replica_set_backup_with_oplog_and_restore()
    {
        using var harness = new Harness();
        // Share the whole harness folder: the staging folder must be visible inside the container too.
        var shared = harness.Root;

        await using var container = new MongoDbBuilder("mongo:7")
            .WithReplicaSet()
            .WithBindMount(shared, shared)
            .Build();
        await container.StartAsync();

        var client = new MongoClient(container.GetConnectionString());
        var collection = client.GetDatabase("shop").GetCollection<BsonDocument>("orders");
        await collection.InsertManyAsync(Enumerable.Range(1, 3000).Select(i => new BsonDocument { ["_id"] = i, ["total"] = i * 1.5, ["customer"] = $"c{i % 97}" }));

        // mongodump/mongorestore run inside the container; paths are identical thanks to the bind mount.
        var inside = BuildInsideUri(container.GetConnectionString());
        var job = new BackupJob
        {
            Name = "Mongo",
            Source = { Kind = SourceKind.MongoDb, MongoDb = { MongodumpPath = harness.DockerExecWrapper(container.Id, "mongodump"), ConnectionString = inside, UseOplog = true } },
            Destinations = [new DestinationDefinition { Name = "Local", LocalFolder = { Path = harness.Dir("target") } }],
            Retry = { MaxAttempts = 1 },
        };

        var run = await harness.BackupAsync(job);

        var restored = Path.Combine(shared, "restored");
        await RestoreService.RestoreFromFileAsync(Path.Combine(job.Destinations[0].LocalFolder.Path, run.FileName!), new RestoreRequest(restored), null, CancellationToken.None);
        var archive = Directory.GetFiles(restored, "*.archive", SearchOption.AllDirectories).Single();

        await MongoDbRestorer.RestoreAsync(harness.DockerExecWrapper(container.Id, "mongorestore"), inside, archive, "shop", "shop_restored", drop: true, CancellationToken.None);

        var copy = client.GetDatabase("shop_restored").GetCollection<BsonDocument>("orders");
        Assert.Equal(3000, await copy.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        var sum = await copy.Aggregate().Group(new BsonDocument { ["_id"] = BsonNull.Value, ["t"] = new BsonDocument("$sum", "$total") }).FirstAsync();
        Assert.Equal(Enumerable.Range(1, 3000).Sum(i => i * 1.5), sum["t"].ToDouble());
    }

    private static string BuildInsideUri(string hostUri)
    {
        // Same credentials, but reach mongod on its own loopback interface from inside the container.
        var url = new MongoUrlBuilder(hostUri) { Server = new MongoServerAddress("127.0.0.1", 27017), DirectConnection = true };
        return url.ToString();
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> FingerprintAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CONCAT(COUNT(*), ':', CHECKSUM_AGG(BINARY_CHECKSUM(Id, Name, Payload))) FROM Items";
        return (string)(await command.ExecuteScalarAsync())!;
    }
}

public class SqlRestoreDrillTests
{
    [DockerFact]
    public async Task Sql_restore_drill_restores_into_temp_database_and_runs_checkdb()
    {
        using var harness = new Harness();
        var shared = harness.Dir("shared");
        await using var container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
            .WithBindMount(shared, shared)
            .WithCreateParameterModifier(p => p.User = "root")
            .Build();
        await container.StartAsync();

        var connectionString = container.GetConnectionString();
        await using (var connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE DATABASE DrillDb; EXEC('USE DrillDb; CREATE TABLE T (Id int PRIMARY KEY); INSERT INTO T VALUES (1),(2),(3);')";
            await command.ExecuteNonQueryAsync();
        }

        var job = new BackupJob
        {
            Name = "SQL drill",
            Source = { Kind = SourceKind.SqlServer, SqlServer = { ConnectionString = connectionString, Databases = ["DrillDb"], BackupDirectory = shared } },
            Destinations = [new DestinationDefinition { Name = "Local", LocalFolder = { Path = harness.Dir("target") } }],
            RestoreDrill = { Enabled = true, CheckSqlDatabases = true },
        };
        await harness.BackupAsync(job);

        var drills = new RestoreDrillRunner(harness.Runs, harness.Settings, new NT.Storix.Core.Destinations.DestinationFactory(), [], Microsoft.Extensions.Logging.Abstractions.NullLogger<RestoreDrillRunner>.Instance);
        var drill = await drills.RunAsync(job, CancellationToken.None);

        Assert.True(drill.Status == RunStatus.Succeeded, drill.Log);
        Assert.Contains("DBCC CHECKDB passed", drill.Log);

        await using var check = new SqlConnection(connectionString);
        await check.OpenAsync();
        await using var count = check.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM sys.databases WHERE name LIKE 'storix_drill_%'";
        Assert.Equal(0, (int)(await count.ExecuteScalarAsync())!);
    }
}
