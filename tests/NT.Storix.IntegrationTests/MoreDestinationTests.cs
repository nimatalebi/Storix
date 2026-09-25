using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DotNet.Testcontainers.Builders;
using NT.Storix.Core.Models;

namespace NT.Storix.IntegrationTests;

public class MoreDestinationTests
{
    internal static bool HasRclone()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("rclone", "version") { RedirectStandardOutput = true, UseShellExecute = false })!;
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [DockerFact]
    public async Task Azure_blob_backup_restore_and_retention()
    {
        await using var container = new ContainerBuilder("mcr.microsoft.com/azure-storage/azurite")
            .WithCommand("azurite-blob", "--blobHost", "0.0.0.0", "--skipApiVersionCheck", "--loose")
            .WithPortBinding(10000, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Azurite Blob service successfully listens"))
            .Build();
        await container.StartAsync();

        // Well-known Azurite development account.
        var connection = "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;" +
                         "AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;" +
                         $"BlobEndpoint=http://{container.Hostname}:{container.GetMappedPublicPort(10000)}/devstoreaccount1;";

        using var harness = new Harness();
        var destination = new DestinationDefinition
        {
            Name = "Azure",
            Kind = DestinationKind.AzureBlob,
            AzureBlob = { ConnectionString = connection, Container = "backups", Prefix = "servers/web01" },
        };
        var job = Harness.FilesJob(harness.CreateSampleFiles(), destination);
        job.Retention.KeepLast = 1;

        await harness.RoundTripAsync(job);
        await Task.Delay(1100);
        await harness.BackupAsync(job);
        Assert.Single(await harness.Restore.ListBackupsAsync(job, destination, CancellationToken.None));
    }

    [RcloneFact]
    public async Task WebDav_and_rclone_destinations()
    {
        using var harness = new Harness();
        var served = harness.Dir("webdav-root");
        var port = FreePort();
        using var server = Process.Start(new ProcessStartInfo("rclone", $"serve webdav \"{served}\" --addr 127.0.0.1:{port} --user storix --pass secret")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        })!;
        try
        {
            await WaitForPortAsync(port);

            var webdav = new DestinationDefinition
            {
                Name = "WebDAV",
                Kind = DestinationKind.WebDav,
                WebDav = { Url = $"http://127.0.0.1:{port}/nested/backups", UserName = "storix", Password = "secret" },
            };
            await harness.RoundTripAsync(Harness.FilesJob(harness.CreateSampleFiles(), webdav));
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(served, "nested", "backups")));

            // rclone destination against a local path (the ":local:" on-the-fly backend).
            var rclone = new DestinationDefinition
            {
                Name = "rclone",
                Kind = DestinationKind.Rclone,
                Rclone = { Remote = ":local:" + harness.Dir("rclone-target") },
            };
            var job = Harness.FilesJob(Path.Combine(harness.Root, "source"), rclone);
            job.Retention.KeepLast = 1;
            await harness.RoundTripAsync(job);
            await Task.Delay(1100);
            await harness.BackupAsync(job);
            Assert.Single(await harness.Restore.ListBackupsAsync(job, rclone, CancellationToken.None));
        }
        finally
        {
            server.Kill(entireProcessTree: true);
        }
    }

    private static async Task WaitForPortAsync(int port)
    {
        for (var i = 0; i < 100; i++)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(100);
            }
        }

        throw new TimeoutException("The WebDAV server did not start.");
    }
}

/// <summary>Integration test that also needs rclone on PATH.</summary>
public sealed class RcloneFactAttribute : FactAttribute
{
    public RcloneFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(DockerFactAttribute.EnvironmentVariable) != "1")
        {
            Skip = $"Integration test: set {DockerFactAttribute.EnvironmentVariable}=1.";
        }
        else if (!MoreDestinationTests.HasRclone())
        {
            Skip = "rclone is not installed.";
        }
    }
}
