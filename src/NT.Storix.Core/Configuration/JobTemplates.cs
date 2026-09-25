using NT.Storix.Core.Models;
using NT.Storix.Core.Sources;

namespace NT.Storix.Core.Configuration;

/// <summary>Ready-made job setups for common scenarios.</summary>
public sealed record JobTemplate(string Name, string Description, Func<BackupJob> Create)
{
    public static IReadOnlyList<JobTemplate> All { get; } =
    [
        new("SQL Server nightly to S3",
            "Full backup of SQL Server databases every night at 01:00, encrypted, to S3-compatible storage. Keeps 14 daily and 12 monthly backups; restore drill every week.",
            () => new BackupJob
            {
                Name = "SQL Server nightly",
                Schedule = { Kind = ScheduleKind.Daily, TimeOfDay = new TimeSpan(1, 0, 0) },
                Source = { Kind = SourceKind.SqlServer },
                Processing = { Encrypt = true, Compression = ArchiveCompression.Optimal },
                Destinations = [new DestinationDefinition { Name = "S3", Kind = DestinationKind.S3 }],
                Retention = { KeepLast = 0, KeepDaily = 14, KeepMonthly = 12 },
                RestoreDrill = { Enabled = true, EveryDays = 7 },
                Notifications = { OnFailure = true, AlertIfNoSuccessForHours = 36 },
            }),
        new("SQL Server point-in-time (log backups)",
            "Transaction-log backup every hour. Use together with full (weekly, COPY_ONLY off) and differential (daily) jobs.",
            () => new BackupJob
            {
                Name = "SQL Server log",
                Schedule = { Kind = ScheduleKind.Cron, CronExpression = "0 * * * *" },
                Source = { Kind = SourceKind.SqlServer, SqlServer = { BackupType = SqlBackupType.Log, CopyOnly = false } },
                Processing = { Encrypt = true },
                Destinations = [new DestinationDefinition { Name = "Local", Kind = DestinationKind.LocalFolder }],
                Retention = { KeepLast = 0, KeepDays = 14 },
                Notifications = { OnFailure = true, AlertIfNoSuccessForHours = 3 },
            }),
        new("Website files weekly to SFTP",
            "IIS website folder every Friday at 23:00 to an SFTP server; logs and temp files excluded.",
            () => new BackupJob
            {
                Name = "Website files",
                Schedule = { Kind = ScheduleKind.Weekly, TimeOfDay = new TimeSpan(23, 0, 0), DaysOfWeek = [DayOfWeek.Friday] },
                Source = { Kind = SourceKind.Files, Files = { Paths = [@"C:\inetpub\wwwroot"], ExcludePatterns = ["*.log", "*.tmp", "App_Data\\Temp"], UseVss = true } },
                Processing = { Encrypt = true },
                Destinations = [new DestinationDefinition { Name = "SFTP", Kind = DestinationKind.Sftp }],
                Retention = { KeepLast = 8 },
            }),
        new("Documents daily to NAS",
            "User documents every day at 20:00 to a network share (UNC), using shadow copies for open files.",
            () => new BackupJob
            {
                Name = "Documents",
                Schedule = { Kind = ScheduleKind.Daily, TimeOfDay = new TimeSpan(20, 0, 0) },
                Source = { Kind = SourceKind.Files, Files = { Paths = [@"%USERPROFILE%\Documents"], UseVss = true } },
                Destinations = [new DestinationDefinition { Name = "NAS", Kind = DestinationKind.LocalFolder, LocalFolder = { Path = @"\\nas\backups" } }],
                Retention = { KeepLast = 7, KeepWeekly = 4, KeepMonthly = 6 },
            }),
        new("MongoDB replica set daily to Google Drive",
            "Consistent mongodump with --oplog every night at 02:00 to Google Drive, split into 1 GB volumes.",
            () => new BackupJob
            {
                Name = "MongoDB nightly",
                Schedule = { Kind = ScheduleKind.Daily, TimeOfDay = new TimeSpan(2, 0, 0) },
                Source = { Kind = SourceKind.MongoDb, MongoDb = { UseOplog = true } },
                Processing = { Encrypt = true, SplitSizeMb = 1024 },
                Destinations = [new DestinationDefinition { Name = "Google Drive", Kind = DestinationKind.GoogleDrive }],
                Retention = { KeepLast = 7 },
            }),
    ];
}
