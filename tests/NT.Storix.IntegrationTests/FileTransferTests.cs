using DotNet.Testcontainers.Builders;
using NT.Storix.Core.Models;
using NT.Storix.Core.Processing;

namespace NT.Storix.IntegrationTests;

public class FileTransferTests
{
    [DockerFact]
    public async Task Sftp_backup_restore_and_retention()
    {
        await using var container = new ContainerBuilder("atmoz/sftp:alpine")
            .WithCommand("storix:secret:::upload")
            .WithPortBinding(22, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Server listening on"))
            .Build();
        await container.StartAsync();

        using var harness = new Harness();
        var destination = new DestinationDefinition
        {
            Name = "SFTP",
            Kind = DestinationKind.Sftp,
            Sftp = { Host = container.Hostname, Port = container.GetMappedPublicPort(22), UserName = "storix", Password = "secret", RemotePath = "upload/storix/nightly" },
        };
        var job = Harness.FilesJob(harness.CreateSampleFiles(), destination);
        job.Retention.KeepLast = 2;

        await harness.RoundTripAsync(job);

        // Two more runs: retention keeps the last two archives (plus their checksums).
        await Task.Delay(1100);
        await harness.BackupAsync(job);
        await Task.Delay(1100);
        await harness.BackupAsync(job);

        var backups = await harness.Restore.ListBackupsAsync(job, destination, CancellationToken.None);
        Assert.Equal(2, backups.Count);
        Assert.All(backups, b => Assert.Contains(b.Files, f => f.EndsWith(Checksum.SidecarExtension)));
    }

    [DockerFact]
    public async Task Ftp_backup_and_restore()
    {
        // Passive data ports must be published 1:1 because the server announces them to the client.
        var builder = new ContainerBuilder("delfer/alpine-ftp-server")
            .WithEnvironment("USERS", "storix|secret")
            .WithEnvironment("ADDRESS", "127.0.0.1")
            .WithEnvironment("MIN_PORT", "21100")
            .WithEnvironment("MAX_PORT", "21110")
            .WithPortBinding(21, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(21));
        for (var port = 21100; port <= 21110; port++)
        {
            builder = builder.WithPortBinding(port, port);
        }

        await using var container = builder.Build();
        await container.StartAsync();

        using var harness = new Harness();
        var destination = new DestinationDefinition
        {
            Name = "FTP",
            Kind = DestinationKind.Ftp,
            Ftp = { Host = "127.0.0.1", Port = container.GetMappedPublicPort(21), UserName = "storix", Password = "secret", Encryption = FtpEncryption.None, RemotePath = "/ftp/storix/backups" },
        };

        await harness.RoundTripAsync(Harness.FilesJob(harness.CreateSampleFiles(), destination, encrypt: false));
    }
}
