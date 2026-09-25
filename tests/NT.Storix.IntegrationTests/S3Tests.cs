using Amazon.Runtime;
using Amazon.S3;
using DotNet.Testcontainers.Builders;
using NT.Storix.Core.Models;

namespace NT.Storix.IntegrationTests;

public class S3Tests
{
    [DockerFact]
    // LocalStack 3.8 is the last image usable without a license.
    public async Task S3_compatible_backup_restore_with_multipart_and_retention()
    {
        await using var container = new ContainerBuilder("localstack/localstack:3.8")
            .WithEnvironment("SERVICES", "s3")
            .WithPortBinding(4566, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Ready."))
            .Build();
        await container.StartAsync();

        var endpoint = $"http://{container.Hostname}:{container.GetMappedPublicPort(4566)}";
        using (var admin = new AmazonS3Client(new BasicAWSCredentials("test", "test"), new AmazonS3Config { ServiceURL = endpoint, ForcePathStyle = true, AuthenticationRegion = "us-east-1" }))
        {
            await admin.PutBucketAsync("storix-backups");
        }

        using var harness = new Harness();
        var destination = new DestinationDefinition
        {
            Name = "S3",
            Kind = DestinationKind.S3,
            S3 =
            {
                ServiceUrl = endpoint,
                ForcePathStyle = true,
                AccessKeyId = "test",
                SecretAccessKey = "test",
                BucketName = "storix-backups",
                Prefix = "servers/web01",
                PartSizeMb = 5, // The 5 MB+ sample file is uploaded in several parts.
            },
        };
        var job = Harness.FilesJob(harness.CreateSampleFiles(), destination);
        job.Retention.KeepLast = 1;

        await harness.RoundTripAsync(job);
        await Task.Delay(1100);
        await harness.BackupAsync(job);

        var backups = await harness.Restore.ListBackupsAsync(job, destination, CancellationToken.None);
        Assert.Single(backups);
    }

    [DockerFact]
    public async Task Object_lock_keeps_backups_that_retention_would_delete()
    {
        await using var container = new ContainerBuilder("localstack/localstack:3.8")
            .WithEnvironment("SERVICES", "s3")
            .WithPortBinding(4566, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Ready."))
            .Build();
        await container.StartAsync();

        var endpoint = $"http://{container.Hostname}:{container.GetMappedPublicPort(4566)}";
        using (var admin = new AmazonS3Client(new BasicAWSCredentials("test", "test"), new AmazonS3Config { ServiceURL = endpoint, ForcePathStyle = true, AuthenticationRegion = "us-east-1" }))
        {
            await admin.PutBucketAsync(new Amazon.S3.Model.PutBucketRequest { BucketName = "locked", ObjectLockEnabledForBucket = true });
        }

        using var harness = new Harness();
        var destination = new DestinationDefinition
        {
            Name = "S3 locked",
            Kind = DestinationKind.S3,
            S3 = { ServiceUrl = endpoint, ForcePathStyle = true, AccessKeyId = "test", SecretAccessKey = "test", BucketName = "locked", ObjectLockMode = S3ObjectLockMode.Governance, ObjectLockDays = 1 },
        };
        var job = Harness.FilesJob(harness.CreateSampleFiles(), destination, encrypt: false);
        job.Retention.KeepLast = 1;

        await harness.BackupAsync(job);
        await Task.Delay(1100);
        var second = await harness.BackupAsync(job);

        Assert.Contains("is locked until", second.Log);
        Assert.Equal(2, (await harness.Restore.ListBackupsAsync(job, destination, CancellationToken.None)).Count);
    }
}
