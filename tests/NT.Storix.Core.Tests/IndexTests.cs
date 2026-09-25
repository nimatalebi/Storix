using NT.Storix.Core.Destinations;
using NT.Storix.Core.Engine;
using NT.Storix.Core.Processing;

namespace NT.Storix.Core.Tests;

public class IndexTests
{
    [Fact]
    public async Task Index_is_uploaded_encrypted_and_lists_every_file()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("source/docs/report.pdf", "pdf");
        temp.WriteFile("source/docs/notes.txt", "notes");
        temp.WriteFile("source/images/logo.png", "png");
        var (job, run) = await RestoreTests.BackupAsync(temp, encrypt: true);

        var indexFile = temp.Combine("target", run.FileName + BackupIndex.Extension);
        Assert.True(File.Exists(indexFile));
        Assert.True(AesFileEncryptor.IsEncryptedFile(indexFile));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => BackupIndex.ReadAsync(indexFile, null, CancellationToken.None));

        var restore = new RestoreService(new DestinationFactory());
        var index = await restore.GetIndexAsync(job.Destinations[0], run.FileName!, "pw", CancellationToken.None);

        Assert.Equal(["source/docs/notes.txt", "source/docs/report.pdf", "source/images/logo.png"], index.Entries.Select(e => e.Path).Order());
        Assert.Equal(11, index.TotalSize);
        Assert.Equal(["source/docs/report.pdf"], index.Search("report").Select(e => e.Path));
        Assert.Equal(["source/images/logo.png"], index.Search("*.png").Select(e => e.Path));

        // The index belongs to the backup: retention removes it together with the archive.
        Assert.Contains(run.FileName + BackupIndex.Extension, BackupNaming.ParseBackups(job.FilePrefix, Directory.GetFiles(temp.Combine("target")).Select(Path.GetFileName)!).Single().Files);
    }

    [Fact]
    public async Task Selected_files_and_folders_can_be_restored_alone()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("source/docs/report.pdf", "pdf");
        temp.WriteFile("source/docs/notes.txt", "notes");
        temp.WriteFile("source/images/logo.png", "png");
        var (job, run) = await RestoreTests.BackupAsync(temp, encrypt: false);
        var restore = new RestoreService(new DestinationFactory());

        await restore.RestoreFromDestinationAsync(job.Destinations[0], run.FileName!,
            new RestoreRequest(temp.Combine("one")) { Include = ["source/docs/notes.txt"] }, null, CancellationToken.None);
        Assert.Equal(["notes.txt"], Directory.GetFiles(temp.Combine("one"), "*", SearchOption.AllDirectories).Select(Path.GetFileName));

        var folder = await restore.RestoreFromDestinationAsync(job.Destinations[0], run.FileName!,
            new RestoreRequest(temp.Combine("folder")) { Include = ["source/docs/"] }, null, CancellationToken.None);
        Assert.Equal(2, folder.Files.Count);
        Assert.False(File.Exists(temp.Combine("folder", "source", "images", "logo.png")));
    }

    [Fact]
    public async Task Backups_without_index_are_listed_from_the_zip()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("source/a.txt", "a");
        var (job, run) = await RestoreTests.BackupAsync(temp, encrypt: true);
        File.Delete(temp.Combine("target", run.FileName + BackupIndex.Extension));

        var index = await new RestoreService(new DestinationFactory()).GetIndexAsync(job.Destinations[0], run.FileName!, "pw", CancellationToken.None);
        Assert.Equal(["source/a.txt"], index.Entries.Select(e => e.Path));
    }
}
