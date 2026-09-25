using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using NT.Storix.Core.Destinations;
using NT.Storix.Core.Engine;
using NT.Storix.Core.Models;
using NT.Storix.Core.Monitoring;
using NT.Storix.Core.Persistence;
using NT.Storix.Core.Security;
using NT.Storix.Core.Sources;

namespace NT.Storix.IntegrationTests;

/// <summary>Temporary folder + metadata database + runner, shared by the integration tests.</summary>
internal sealed class Harness : IDisposable
{
    private sealed class PlainProtector : ISecretProtector
    {
        public string? Protect(string? plainText) => plainText;

        public string? Unprotect(string? protectedText) => protectedText;
    }

    public Harness()
    {
        // Under /tmp so it can be bind-mounted into Linux containers; world-writable for container users.
        Root = Path.Combine(Path.GetTempPath(), "storix-it", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        MakeWorldWritable(Root);

        var database = new StorixDatabase(Path.Combine(Root, "storix.db"));
        Settings = new SettingsRepository(database, new PlainProtector());
        Settings.Save(new AppSettings { StagingDirectory = Dir("staging") });
        Runs = new RunRepository(database);
        Runner = new BackupJobRunner(Runs, Settings, new SourceFactory(), new DestinationFactory(), Array.Empty<INotifier>(), NullLogger<BackupJobRunner>.Instance);
        Restore = new RestoreService(new DestinationFactory());
    }

    public string Root { get; }

    public RunRepository Runs { get; }

    public SettingsRepository Settings { get; }

    public BackupJobRunner Runner { get; }

    public RestoreService Restore { get; }

    public string Dir(params string[] parts)
    {
        var path = Path.Combine([Root, .. parts]);
        Directory.CreateDirectory(path);
        MakeWorldWritable(path);
        return path;
    }

    /// <summary>Creates a source folder with text and random binary files and returns it.</summary>
    public string CreateSampleFiles()
    {
        var source = Dir("source");
        File.WriteAllText(Path.Combine(source, "readme.txt"), "Storix integration test");
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        File.WriteAllBytes(Path.Combine(source, "nested", "random.bin"), RandomNumberGenerator.GetBytes(5 * 1024 * 1024 + 17));
        return source;
    }

    public static BackupJob FilesJob(string source, DestinationDefinition destination, bool encrypt = true) => new()
    {
        Name = "Integration",
        Source = { Kind = SourceKind.Files, Files = { Paths = [source] } },
        Processing = { Encrypt = encrypt, EncryptionPassword = encrypt ? "integration-pw" : null },
        Destinations = [destination],
        Retry = { MaxAttempts = 2, InitialDelaySeconds = 1 },
    };

    public async Task<BackupRun> BackupAsync(BackupJob job)
    {
        var run = await Runner.RunAsync(job, RunTrigger.Manual, CancellationToken.None);
        Assert.True(run.Status == RunStatus.Succeeded, $"Backup failed: {run.Message}\n{run.Log}");
        return run;
    }

    /// <summary>Backs up, restores from the destination and compares every file byte by byte.</summary>
    public async Task RoundTripAsync(BackupJob job)
    {
        var source = job.Source.Files.Paths.Single();
        var run = await BackupAsync(job);

        var target = Dir("restored-" + Guid.NewGuid().ToString("N")[..6]);
        var result = await Restore.RestoreFromDestinationAsync(
            job.Destinations[0], run.FileName!, new RestoreRequest(target, job.Processing.EncryptionPassword), null, CancellationToken.None);

        Assert.True(result.ChecksumVerified);
        AssertSameTree(source, Path.Combine(target, new DirectoryInfo(source).Name));
    }

    public static void AssertSameTree(string expected, string actual)
    {
        var expectedFiles = Directory.GetFiles(expected, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(expected, f)).Order().ToList();
        var actualFiles = Directory.GetFiles(actual, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(actual, f)).Order().ToList();
        Assert.Equal(expectedFiles, actualFiles);

        foreach (var file in expectedFiles)
        {
            Assert.True(
                File.ReadAllBytes(Path.Combine(expected, file)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(actual, file))),
                $"File '{file}' differs after restore.");
        }
    }

    /// <summary>Writes an executable shell script that forwards its arguments to a tool inside a container.</summary>
    public string DockerExecWrapper(string containerId, string tool, string? folder = null)
    {
        var path = folder is null ? Path.Combine(Root, $"{tool}-wrapper.sh") : Path.Combine(folder, tool);

        // Password variables are forwarded into the container (docker exec -e NAME copies the value).
        File.WriteAllText(path, $"#!/bin/sh\nexec docker exec -i -e PGPASSWORD -e MYSQL_PWD -e REDISCLI_AUTH {containerId} {tool} \"$@\"\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception)
        {
            // Files created by container users may not be removable; the OS cleans /tmp.
        }
    }

    private static void MakeWorldWritable(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            // rwxrwxrwx + setgid: files created by container users (e.g. SQL Server writes .bak files with mode 0640)
            // inherit the folder's group, so the test process can still read them.
            File.SetUnixFileMode(path, (UnixFileMode)0b111_111_111 | UnixFileMode.SetGroup);
        }
    }
}
