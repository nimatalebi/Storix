using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using MongoDB.Bson;
using MongoDB.Driver;

namespace NT.Storix.Core.Configuration;

/// <summary>An IIS website and the folder of its root application.</summary>
public sealed record IisSite(string Name, string PhysicalPath);

/// <summary>Finds what can be backed up on this server: IIS websites, SQL Server and MongoDB databases.</summary>
public static class ServerDiscovery
{
    public static string IisConfigPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "inetsrv", "config", "applicationHost.config");

    /// <summary>IIS websites of this server (empty when IIS is not installed or the config cannot be read).</summary>
    public static IReadOnlyList<IisSite> IisSites()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(IisConfigPath))
        {
            return [];
        }

        try
        {
            return ParseIisSites(File.ReadAllText(IisConfigPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return [];
        }
    }

    /// <summary>Reads the sites of an applicationHost.config: the physical path of each site's root application.</summary>
    public static IReadOnlyList<IisSite> ParseIisSites(string applicationHostXml)
    {
        var document = XDocument.Parse(applicationHostXml);
        var result = new List<IisSite>();
        foreach (var site in document.Descendants("sites").Elements("site"))
        {
            var name = (string?)site.Attribute("name");
            var root = site.Elements("application").FirstOrDefault(a => ((string?)a.Attribute("path") ?? "/") == "/");
            var directory = root?.Elements("virtualDirectory").FirstOrDefault(v => (string?)v.Attribute("path") == "/");
            var path = (string?)directory?.Attribute("physicalPath");
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(path))
            {
                result.Add(new IisSite(name, Environment.ExpandEnvironmentVariables(path)));
            }
        }

        return result.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>User databases of a SQL Server instance (online, without tempdb).</summary>
    public static async Task<IReadOnlyList<string>> SqlServerDatabasesAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sys.databases WHERE name <> 'tempdb' AND state_desc = 'ONLINE' ORDER BY name";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    /// <summary>Databases of a MongoDB server, without the system databases (admin, local, config).</summary>
    public static async Task<IReadOnlyList<string>> MongoDbDatabasesAsync(string connectionString, CancellationToken cancellationToken)
    {
        var settings = MongoClientSettings.FromConnectionString(connectionString);
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(10);
        using var client = new MongoClient(settings);
        using var cursor = await client.ListDatabaseNamesAsync(cancellationToken);
        var names = await cursor.ToListAsync(cancellationToken);
        return names.Where(n => n is not ("admin" or "local" or "config")).Order(StringComparer.Ordinal).ToList();
    }
}
