using NT.Storix.Core.Configuration;
using NT.Storix.Core.Models;

namespace NT.Storix.Core.Tests;

public class ConfigurationTests
{
    private static BackupJob SampleJob() => new()
    {
        Name = "Nightly SQL",
        Source = new SourceDefinition
        {
            Kind = SourceKind.SqlServer,
            SqlServer = new SqlServerSourceOptions { ConnectionString = "Server=.;Password=db-secret", Databases = ["Sales"] },
        },
        Processing = new ProcessingOptions { Encrypt = true, EncryptionPassword = "archive-secret" },
        Destinations =
        [
            new DestinationDefinition { Kind = DestinationKind.Sftp, Sftp = new SftpOptions { Host = "backup.example.com", UserName = "u", Password = "sftp-secret" } },
        ],
    };

    [Fact]
    public void Export_without_passphrase_strips_secrets()
    {
        var json = ConfigurationPorter.Export([SampleJob()], new AppSettings(), passphrase: null);

        Assert.DoesNotContain("-secret", json);
        Assert.False(ConfigurationPorter.RequiresPassphrase(json));

        var job = Assert.Single(ConfigurationPorter.Import(json, null).Jobs);
        Assert.Equal("Nightly SQL", job.Name);
        Assert.Null(job.Processing.EncryptionPassword);
        Assert.Equal("backup.example.com", job.Destinations[0].Sftp.Host);
    }

    [Fact]
    public void Export_with_passphrase_roundtrips_secrets()
    {
        var original = SampleJob();
        var json = ConfigurationPorter.Export([original], null, "correct horse");

        Assert.DoesNotContain("-secret", json);
        Assert.True(ConfigurationPorter.RequiresPassphrase(json));
        Assert.Throws<UnauthorizedAccessException>(() => ConfigurationPorter.Import(json, "wrong"));

        var job = Assert.Single(ConfigurationPorter.Import(json, "correct horse").Jobs);
        Assert.Equal(original.Id, job.Id);
        Assert.Equal("archive-secret", job.Processing.EncryptionPassword);
        Assert.Equal("sftp-secret", job.Destinations[0].Sftp.Password);
        Assert.Equal("Server=.;Password=db-secret", job.Source.SqlServer.ConnectionString);
    }

    [Fact]
    public void Invalid_file_is_rejected()
    {
        Assert.Throws<InvalidDataException>(() => ConfigurationPorter.Import("{\"hello\":1}", null));
        Assert.Throws<InvalidDataException>(() => ConfigurationPorter.Import("not json", null));
    }
}
