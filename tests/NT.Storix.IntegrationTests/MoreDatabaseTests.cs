using DotNet.Testcontainers.Builders;
using NT.Storix.Core.Engine;
using NT.Storix.Core.Models;
using NT.Storix.Core.Sources;

namespace NT.Storix.IntegrationTests;

public class MoreDatabaseTests
{
    private static string Tools(Harness harness, string containerId, params string[] tools)
    {
        var folder = harness.Dir("tools-" + containerId[..8]);
        foreach (var tool in tools)
        {
            harness.DockerExecWrapper(containerId, tool, folder);
        }

        return folder;
    }

    private static BackupJob Job(Harness harness, Action<SourceDefinition> source) => new()
    {
        Name = "db-" + Guid.NewGuid().ToString("N")[..6],
        Source = new SourceDefinition().Also(source),
        Processing = { Encrypt = true, EncryptionPassword = "pw" },
        Destinations = [new DestinationDefinition { Name = "Local", LocalFolder = { Path = harness.Dir("target") } }],
        Retry = { MaxAttempts = 1 },
    };

    private static async Task<string> RestoreSingleAsync(Harness harness, BackupJob job, BackupRun run, string pattern)
    {
        var folder = Path.Combine(harness.Root, "restored-" + Guid.NewGuid().ToString("N")[..6]);
        await RestoreService.RestoreFromFileAsync(Path.Combine(job.Destinations[0].LocalFolder.Path, run.FileName!), new RestoreRequest(folder, "pw"), null, CancellationToken.None);
        return Directory.GetFiles(folder, pattern, SearchOption.AllDirectories).Single();
    }

    [DockerFact]
    public async Task PostgreSql_dump_and_restore_into_new_database()
    {
        using var harness = new Harness();
        await using var container = new ContainerBuilder("postgres:17-alpine")
            .WithEnvironment("POSTGRES_PASSWORD", "secret")
            .WithBindMount(harness.Root, harness.Root)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("database system is ready to accept connections", o => o.WithTimeout(TimeSpan.FromMinutes(2))))
            .Build();
        await container.StartAsync();
        await Task.Delay(2000); // The server restarts once after initialisation.

        async Task<string> Psql(string db, string sql) =>
            (await container.ExecAsync(["psql", "-U", "postgres", "-d", db, "-tAc", sql])).Stdout.Trim();

        await Psql("postgres", "CREATE DATABASE shop");
        await Psql("shop", "CREATE TABLE orders (id int primary key, total numeric); INSERT INTO orders SELECT g, g * 1.5 FROM generate_series(1, 2500) g;");

        var tools = Tools(harness, container.Id, "pg_dump", "pg_dumpall", "pg_restore", "createdb", "psql");
        var options = new PostgreSqlSourceOptions { PgDumpPath = tools, Host = "127.0.0.1", UserName = "postgres", Password = "secret", Databases = "shop" };
        var job = Job(harness, s => { s.Kind = SourceKind.PostgreSql; s.PostgreSql = options; });

        var run = await harness.BackupAsync(job);
        var dump = await RestoreSingleAsync(harness, job, run, "*.dump");

        await DumpRestorers.RestorePostgreSqlAsync(options, dump, "shop_restored", CancellationToken.None);
        Assert.Equal("2500|4689375.0", await Psql("shop_restored", "SELECT count(*) || '|' || sum(total) FROM orders"));
    }

    [DockerFact]
    public async Task MySql_dump_and_restore()
    {
        using var harness = new Harness();
        await using var container = new ContainerBuilder("mysql:8.4")
            .WithEnvironment("MYSQL_ROOT_PASSWORD", "secret")
            .WithBindMount(harness.Root, harness.Root)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("ready for connections.*port: 3306", o => o.WithTimeout(TimeSpan.FromMinutes(3))))
            .Build();
        await container.StartAsync();

        async Task<string> Mysql(string sql) =>
            (await container.ExecAsync(["mysql", "-uroot", "-psecret", "-N", "-e", sql])).Stdout.Trim();

        await Mysql("CREATE DATABASE shop; CREATE TABLE shop.orders (id int primary key, name varchar(50)); " +
                    "INSERT INTO shop.orders VALUES (1,'a'),(2,'b'),(3,'c'); CREATE PROCEDURE shop.p() SELECT 1;");

        var tools = Tools(harness, container.Id, "mysqldump", "mysql");
        var options = new MySqlSourceOptions { MysqldumpPath = Path.Combine(tools, "mysqldump"), Host = "127.0.0.1", UserName = "root", Password = "secret", Databases = "shop" };
        var job = Job(harness, s => { s.Kind = SourceKind.MySql; s.MySql = options; });

        var run = await harness.BackupAsync(job);
        var sql = await RestoreSingleAsync(harness, job, run, "*.sql");

        await Mysql("DROP DATABASE shop");
        await DumpRestorers.RestoreMySqlAsync(options, sql, CancellationToken.None);
        Assert.Equal("3", await Mysql("SELECT COUNT(*) FROM shop.orders"));
        Assert.Contains("p", await Mysql("SELECT ROUTINE_NAME FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA='shop'"));
    }

    [DockerFact]
    public async Task Redis_rdb_snapshot_is_valid()
    {
        using var harness = new Harness();
        await using var container = new ContainerBuilder("redis:7-alpine")
            .WithCommand("redis-server", "--requirepass", "secret")
            .WithBindMount(harness.Root, harness.Root)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Ready to accept connections"))
            .Build();
        await container.StartAsync();
        await container.ExecAsync(["redis-cli", "-a", "secret", "SET", "greeting", "hello"]);

        var tools = Tools(harness, container.Id, "redis-cli");
        var job = Job(harness, s =>
        {
            s.Kind = SourceKind.Redis;
            s.Redis = new RedisSourceOptions { RedisCliPath = Path.Combine(tools, "redis-cli"), Host = "127.0.0.1", Password = "secret" };
        });

        var run = await harness.BackupAsync(job);
        var rdb = await RestoreSingleAsync(harness, job, run, "*.rdb");

        Assert.StartsWith("REDIS", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(rdb), 0, 5));
        var check = await container.ExecAsync(["redis-check-rdb", rdb]);
        Assert.Contains("RDB looks OK", check.Stdout + check.Stderr);
    }
}

internal static class ObjectExtensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
