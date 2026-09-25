using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using NT.Storix.Core.Destinations;
using NT.Storix.Core.Engine;
using NT.Storix.Core.Models;
using NT.Storix.Core.Monitoring;
using NT.Storix.Core.Persistence;
using NT.Storix.Core.Processing;
using NT.Storix.Core.Security;
using NT.Storix.Core.Sources;

namespace NT.Storix.Core.Tests;

public class PublicKeyTests
{
    // RSA-4096 generation is slow: share one key pair across the tests of this class.
    private static readonly Lazy<(string Public, string Private)> Keys = new(() => PrivateKeySecret.GenerateKeyPair("key-passphrase"));

    [Fact]
    public async Task Public_key_encryption_round_trips_only_with_the_private_key()
    {
        using var temp = new TempDirectory();
        var data = RandomNumberGenerator.GetBytes(200_000);
        await File.WriteAllBytesAsync(temp.Combine("plain.bin"), data);

        await AesFileEncryptor.EncryptWithPublicKeyAsync(temp.Combine("plain.bin"), temp.Combine("enc.aes"), Keys.Value.Public, CancellationToken.None);
        Assert.True(AesFileEncryptor.UsesPublicKey(temp.Combine("enc.aes")));

        // A password cannot open it, and neither can another key pair.
        await Assert.ThrowsAsync<CryptographicException>(() => AesFileEncryptor.DecryptAsync(temp.Combine("enc.aes"), temp.Combine("x1"), "some password", CancellationToken.None));
        var other = PrivateKeySecret.GenerateKeyPair(null);
        var otherError = await Assert.ThrowsAsync<CryptographicException>(() =>
            AesFileEncryptor.DecryptAsync(temp.Combine("enc.aes"), temp.Combine("x2"), PrivateKeySecret.ForPrivateKey(other.PrivateKeyPem, null), CancellationToken.None));
        Assert.Contains("different key pair", otherError.Message);

        // The encrypted private key needs its passphrase.
        await Assert.ThrowsAnyAsync<CryptographicException>(() =>
            AesFileEncryptor.DecryptAsync(temp.Combine("enc.aes"), temp.Combine("x3"), PrivateKeySecret.ForPrivateKey(Keys.Value.Private, null), CancellationToken.None));

        await AesFileEncryptor.DecryptAsync(temp.Combine("enc.aes"), temp.Combine("out.bin"), PrivateKeySecret.ForPrivateKey(Keys.Value.Private, "key-passphrase"), CancellationToken.None);
        Assert.Equal(data, await File.ReadAllBytesAsync(temp.Combine("out.bin")));
    }

    [Fact]
    public async Task Tampering_with_a_public_key_backup_is_detected()
    {
        using var temp = new TempDirectory();
        await File.WriteAllBytesAsync(temp.Combine("plain.bin"), RandomNumberGenerator.GetBytes(50_000));
        await AesFileEncryptor.EncryptWithPublicKeyAsync(temp.Combine("plain.bin"), temp.Combine("enc.aes"), Keys.Value.Public, CancellationToken.None);

        var bytes = await File.ReadAllBytesAsync(temp.Combine("enc.aes"));
        bytes[^100] ^= 1;
        await File.WriteAllBytesAsync(temp.Combine("enc.aes"), bytes);

        await Assert.ThrowsAsync<CryptographicException>(() =>
            AesFileEncryptor.VerifyAsync(temp.Combine("enc.aes"), PrivateKeySecret.ForPrivateKey(Keys.Value.Private, "key-passphrase"), CancellationToken.None));
    }

    [Fact]
    public async Task Job_in_public_key_mode_backs_up_without_the_private_key_and_restores_with_it()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("source/secret.txt", "top secret");
        var database = new StorixDatabase(temp.Combine("s.db"));
        var settings = new SettingsRepository(database, new MachineSecretProtector());
        settings.Save(new AppSettings { StagingDirectory = temp.Combine("staging") });
        var runs = new RunRepository(database);
        var job = new BackupJob
        {
            Name = "Offline key",
            Source = { Files = { Paths = [temp.Combine("source")] } },
            Processing = { Encrypt = true, EncryptionMode = EncryptionMode.PublicKey, PublicKeyPem = Keys.Value.Public },
            Destinations = [new DestinationDefinition { LocalFolder = { Path = temp.Combine("dst") } }],
        };
        Assert.Empty(BackupJobRunner.GetValidationErrors(job));

        var runner = new BackupJobRunner(runs, settings, new SourceFactory(), new DestinationFactory(), Array.Empty<INotifier>(), NullLogger<BackupJobRunner>.Instance);
        var run = await runner.RunAsync(job, RunTrigger.Manual, CancellationToken.None);
        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Contains("RSA public key", run.Log);

        // Restore with the private key file (the password field carries its passphrase).
        await File.WriteAllTextAsync(temp.Combine("private.pem"), Keys.Value.Private);
        var secret = EncryptionSecret.Combine("key-passphrase", temp.Combine("private.pem"));
        var restore = new RestoreService(new DestinationFactory());
        var index = await restore.GetIndexAsync(job.Destinations[0], run.FileName!, secret, CancellationToken.None);
        Assert.Equal(["source/secret.txt"], index.Entries.Select(e => e.Path));
        await restore.RestoreFromDestinationAsync(job.Destinations[0], run.FileName!, new RestoreRequest(temp.Combine("r"), secret), null, CancellationToken.None);
        Assert.Equal("top secret", File.ReadAllText(temp.Combine("r", "source", "secret.txt")));

        // A restore drill on the server (no private key) still checks the backup's integrity.
        var drill = await new RestoreDrillRunner(runs, settings, new DestinationFactory(), Array.Empty<INotifier>(), NullLogger<RestoreDrillRunner>.Instance)
            .RunAsync(job, CancellationToken.None);
        Assert.Equal(RunStatus.Succeeded, drill.Status);
        Assert.Contains("checksum verified", drill.Message);
    }

    [Fact]
    public void Invalid_public_key_is_reported()
    {
        var job = new BackupJob { Processing = { Encrypt = true, EncryptionMode = EncryptionMode.PublicKey, PublicKeyPem = "not a key" } };
        Assert.Contains(BackupJobRunner.GetValidationErrors(job), e => e.Contains("public key"));
        Assert.Equal(16, PrivateKeySecret.Fingerprint(Keys.Value.Public).Length);
    }
}
