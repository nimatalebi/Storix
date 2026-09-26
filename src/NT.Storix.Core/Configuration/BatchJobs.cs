using NT.Storix.Core.Models;
using NT.Storix.Core.Sources;

namespace NT.Storix.Core.Configuration;

public enum BatchKind
{
    /// <summary>Website folders (IIS sites or any folder), one job per site.</summary>
    Websites,
    MongoDbDatabases,
    SqlServerDatabases,
    /// <summary>Plain folders, one job per folder.</summary>
    Folders,
}

public enum RetentionPreset
{
    /// <summary>7 daily, 4 weekly, 6 monthly.</summary>
    Standard,
    /// <summary>Last 7 backups.</summary>
    Short,
    /// <summary>14 daily, 8 weekly, 12 monthly, 3 yearly.</summary>
    Long,
}

/// <param name="Name">Shown in the job name (site or database name).</param>
/// <param name="Value">Folder path or database name.</param>
public sealed record BatchItem(string Name, string Value);

/// <summary>Everything the "several backups at once" wizard asks for.</summary>
public sealed class BatchOptions
{
    public BatchKind Kind { get; set; }

    public List<BatchItem> Items { get; set; } = [];

    /// <summary>MongoDB or SQL Server connection string.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Shared by every job (same destination id, so its sign-in and statistics are shared).</summary>
    public List<DestinationDefinition> Destinations { get; set; } = [];

    public ScheduleKind Schedule { get; set; } = ScheduleKind.Daily;

    public DayOfWeek WeeklyDay { get; set; } = DayOfWeek.Friday;

    public TimeSpan FirstStart { get; set; } = new(1, 0, 0);

    /// <summary>Minutes between the start of one job and the next, so they do not all run at once.</summary>
    public int StaggerMinutes { get; set; } = 15;

    public RetentionPreset Retention { get; set; } = RetentionPreset.Standard;

    public string? EncryptionPassword { get; set; }

    public bool RecoveryInfoConfirmed { get; set; }

    public bool NotifyOnFailure { get; set; } = true;
}

/// <summary>Creates one job per website, database or folder with shared settings.</summary>
public static class BatchJobs
{
    /// <summary>Files that do not belong in a website backup.</summary>
    public static readonly IReadOnlyList<string> WebsiteExcludes =
        ["*.log", "*.tmp", "logs", "Logs", "temp", "tmp", "cache", ".cache", "node_modules", ".git", "App_Data\\Temp", "wp-content\\cache"];

    public static IReadOnlyList<BackupJob> Create(BatchOptions options)
    {
        if (options.Items.Count == 0)
        {
            throw new ArgumentException("Choose at least one item.", nameof(options));
        }

        var jobs = new List<BackupJob>();
        for (var i = 0; i < options.Items.Count; i++)
        {
            var item = options.Items[i];
            var start = options.FirstStart + TimeSpan.FromMinutes(Math.Max(0, options.StaggerMinutes) * i);
            var job = new BackupJob
            {
                Name = options.Kind switch
                {
                    BatchKind.Websites => $"Website - {item.Name}",
                    BatchKind.MongoDbDatabases => $"MongoDB - {item.Name}",
                    BatchKind.SqlServerDatabases => $"SQL Server - {item.Name}",
                    _ => $"Files - {item.Name}",
                },
                Schedule =
                {
                    Kind = options.Schedule,
                    TimeOfDay = new TimeSpan(start.Hours, start.Minutes, 0),
                    DaysOfWeek = options.Schedule == ScheduleKind.Weekly ? [options.WeeklyDay] : [],
                },
                Destinations = options.Destinations.Select(StorixJson.Clone).ToList(),
                Retention = RetentionFor(options.Retention),
                Notifications = { OnFailure = options.NotifyOnFailure, AlertIfNoSuccessForHours = options.Schedule == ScheduleKind.Weekly ? 8 * 24 : 36 },
            };

            if (!string.IsNullOrEmpty(options.EncryptionPassword))
            {
                job.Processing.Encrypt = true;
                job.Processing.EncryptionPassword = options.EncryptionPassword;
                job.Processing.RecoveryInfoConfirmed = options.RecoveryInfoConfirmed;
            }

            switch (options.Kind)
            {
                case BatchKind.Websites:
                    job.Source.Files = new FileSourceOptions { Paths = [item.Value], ExcludePatterns = [.. WebsiteExcludes], UseVss = OperatingSystem.IsWindows() };
                    break;
                case BatchKind.Folders:
                    job.Source.Files = new FileSourceOptions { Paths = [item.Value], UseVss = OperatingSystem.IsWindows() };
                    break;
                case BatchKind.MongoDbDatabases:
                    job.Source.Kind = SourceKind.MongoDb;
                    job.Source.MongoDb = new MongoDbSourceOptions { ConnectionString = options.ConnectionString, Database = item.Value };
                    job.Processing.Compression = ArchiveCompression.Zstd;
                    break;
                case BatchKind.SqlServerDatabases:
                    job.Source.Kind = SourceKind.SqlServer;
                    job.Source.SqlServer.ConnectionString = options.ConnectionString;
                    job.Source.SqlServer.Databases = [item.Value];
                    job.RestoreDrill = new RestoreDrillOptions { Enabled = true, EveryDays = 7 };
                    break;
            }

            jobs.Add(job);
        }

        return jobs;
    }

    public static RetentionPolicy RetentionFor(RetentionPreset preset) => preset switch
    {
        RetentionPreset.Short => new RetentionPolicy { KeepLast = 7 },
        RetentionPreset.Long => new RetentionPolicy { KeepLast = 0, KeepDaily = 14, KeepWeekly = 8, KeepMonthly = 12, KeepYearly = 3 },
        _ => new RetentionPolicy { KeepLast = 0, KeepDaily = 7, KeepWeekly = 4, KeepMonthly = 6 },
    };
}
