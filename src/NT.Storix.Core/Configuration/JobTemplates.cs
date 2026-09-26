using NT.Storix.Core.Models;
using NT.Storix.Core.Sources;

namespace NT.Storix.Core.Configuration;

/// <summary>Ready-made job setups for common scenarios. Everything can be changed after creating the job.</summary>
public sealed record JobTemplate(string Name, string Description, Func<BackupJob> Create)
{
    public const string Websites = "Websites";
    public const string Databases = "Databases";
    public const string Files = "Files and folders";
    public const string Server = "Server and virtual machines";
    public const string Offsite = "Off-site copies";

    /// <summary>Menu group of the template.</summary>
    public string Category { get; init; } = Files;

    private static readonly string[] WebExcludes = [.. BatchJobs.WebsiteExcludes];

    private static TimeSpan At(int hour, int minute = 0) => new(hour, minute, 0);

    private static DestinationDefinition Destination(string name, DestinationKind kind) => new() { Name = name, Kind = kind };

    private static JobTemplate T(string category, string name, string description, Func<BackupJob> create) =>
        new(name, description, create) { Category = category };

    public static IReadOnlyList<JobTemplate> All { get; } =
    [
        // ------------------------------------------------------------------ Websites
        T(Websites, "Website (IIS) daily to Google Drive",
            "One IIS website folder every night at 02:00 to Google Drive, encrypted. Logs, caches, temp files, node_modules and .git are excluded; open files are read from a shadow copy. Keeps 7 daily, 4 weekly and 6 monthly backups.",
            () => new BackupJob
            {
                Name = "Website",
                Schedule = { Kind = ScheduleKind.Daily, TimeOfDay = At(2) },
                Source = { Files = { Paths = [@"C:\inetpub\wwwroot"], ExcludePatterns = [.. WebExcludes], UseVss = true } },
                Processing = { Encrypt = true, Compression = ArchiveCompression.Zstd },
                Destinations = [Destination("Google Drive", DestinationKind.GoogleDrive)],
                Retention = BatchJobs.RetentionFor(RetentionPreset.Standard),
                Notifications = { OnFailure = true, AlertIfNoSuccessForHours = 36 },
            }),
        T(Websites, "Website weekly to SFTP",
            "IIS website folder every Friday at 23:00 to an SFTP server; logs and temp files excluded. Keeps 8 backups.",
            () => new BackupJob
            {
                Name = "Website files",
                Schedule = { Kind = ScheduleKind.Weekly, TimeOfDay = At(23), DaysOfWeek = [DayOfWeek.Friday] },
                Source = { Files = { Paths = [@"C:\inetpub\wwwroot"], ExcludePatterns = [.. WebExcludes], UseVss = true } },
                Processing = { Encrypt = true },
                Destinations = [Destination("SFTP", DestinationKind.Sftp)],
                Retention = { KeepLast = 8 },
            }),
        T(Websites, "Busy website hourly (incremental) to NAS",
            "For sites with frequent uploads: only changed files every hour, a full backup every 7 days, to a network share. Keeps two weeks of hourly history.",
            () => new BackupJob
            {
                Name = "Website (hourly)",
                Schedule = { Kind = ScheduleKind.Cron, CronExpression = "15 * * * *" },
                Source = { Files = { Paths = [@"C:\inetpub\wwwroot"], ExcludePatterns = [.. WebExcludes], UseVss = true, Incremental = true, FullBackupEveryDays = 7 } },
                Destinations = [new DestinationDefinition { Name = "NAS", LocalFolder = { Path = @"\\nas\backups\website" } }],
                Retention = { KeepLast = 0, KeepDays = 14 },
            }),
        T(Websites, "WordPress - files",
            "WordPress folder (themes, plugins, uploads, wp-config.php) every night at 02:00 to Google Drive, caches excluded. Pair it with \"WordPress - database\".",
            () => new BackupJob
            {
                Name = "WordPress files",
                Schedule = { Kind = ScheduleKind.Daily, TimeOfDay = At(2) },
                Source = { Files = { Paths = [@"C:\inetpub\wordpress"], ExcludePatterns = [.. WebExcludes, "wp-content\\upgrade", "wp-content\\backup*"], UseVss = true } },
                Processing = { Encrypt = true, Compression = ArchiveCompression.Zstd },
                Destinations = [Destination("Google Drive", DestinationKind.GoogleDrive)],
                Retention = BatchJobs.RetentionFor(RetentionPreset.Standard),
            }),
        T(Websites, "WordPress - database (MySQL/MariaDB)",
            "The WordPress MySQL/MariaDB database every night at 01:45 (consistent dump without locking) to Google Drive.",
            () => new BackupJob
            {
                Name = "WordPress database",
                Schedule = { Kind = ScheduleKind.Daily, TimeOfDay = At(1, 45) },
                Source = { Kind = SourceKind.MySql, MySql = { Databases = "wordpress", SingleTransaction = true } },
                Processing = { Encrypt = true, Compression = ArchiveCompression.Zstd },
                Destinations = [Destination("Google Drive", DestinationKind.GoogleDrive)],
                Retention = BatchJobs.RetentionFor(RetentionPreset.Standard),
                Notifications = { OnFailure = true, AlertIfNoSuccessForHours = 36 },
            }),
        T(Websites, "Linux web root (/var/www) to S3",
            "For the Linux agent: /var/www every night at 03:00 to S3-compatible storage, logs and caches excluded.",
            () => new BackupJob
            {
                Name = "Web root",
                Schedule = { Kind = ScheduleKind.Daily, TimeOfDay = At(3) },
                Source = { Files = { Paths = ["/var/www"], ExcludePatterns = [.. WebExcludes] } },
                Processing = { Encrypt = true, Compression = ArchiveCompression.Zstd },
                Destinations = [Destination("S3", DestinationKind.S3)],
                Retention = BatchJobs.RetentionFor(RetentionPreset.Standard),
            }),

        // ------------------------------------------------------------------ Databases
        T(Databases, "MongoDB database daily to Google Drive",
            "One MongoDB database (mongodump) every night at 02:30 to Google Drive, zstd-compressed and encrypted. Set the connection string and the database name.",
            () => new BackupJob
            {
                Name = "MongoDB",
                Schedule = { Kind = ScheduleKind.Daily, TimeOfDay = At(2, 30) },
                Source = { Kind = SourceKind.MongoDb, MongoDb = { Database = "mydb" } },
                Processing = { Encrypt = true, Compression = ArchiveCompression.Zstd },
                Destinations = [Destination("Google Drive", DestinationKind.GoogleDrive)],
                Retention = BatchJobs.RetentionFor(RetentionPreset.Standard),
                Notifications = { OnFailure = true, AlertIfNoSuccessForHours = 36 },
            }),
        T(Databases, "MongoDB replica set (all databases, --oplog) to Google Drive",
            "Point-in-time consistent mongodump of every database with --oplog every night at 02:00, split into 1 GB volumes.",
            () => new BackupJob
            {
                Name = "MongoDB nightly",
                Schedule = { Kind = ScheduleKind.Daily, TimeOfDay = At(2) },
                Source = { Kind = SourceKind.MongoDb, MongoDb = { UseOplog = true } },
                Processing = { Encrypt = true, SplitSizeMb = 1024 },
                Destinations = [Destination("Google Drive", DestinationKind.GoogleDrive)],
                Retention = { KeepLast = 7 },
            }),
        T(Databases, "SQL Server nightly to S3",
            "Full backup of SQL Server databases every night at 01:00, encrypted, to S3-compatible storage. Keeps 14 daily and 12 monthly backups; restore drill every week.",
            () => new BackupJob
            {
                Name = "SQL Server nightly",
                Schedule = { Kind = ScheduleKind.Daily, TimeOfDay = At(1) },
                Source = { Kind = SourceKind.SqlServer },
                Processing = { Encrypt = true, Compression = ArchiveCompression.Optimal },
                Destinations = [Destination("S3", DestinationKind.S3)],
                Retention = { KeepLast = 0, KeepDaily = 14, KeepMonthly = 12 },
                RestoreDrill = { Enabled = true, EveryDays = 7 },
                Notifications = { OnFailure = true, AlertIfNoSuccessForHours = 36 },
            }),
        T(Databases, "SQL Server point-in-time 1/3: full (weekly)",
            "Full backup every Sunday at 01:00 that starts the restore chain (COPY_ONLY off). Use with the differential and log templates.",
            () => new BackupJob
            {
                Name = "SQL Server full",
                Schedule = { Kind = ScheduleKind.Weekly, TimeOfDay = At(1), DaysOfWeek = [DayOfWeek.Sunday] },
                Source = { Kind = SourceKind.SqlServer, SqlServer = { BackupType = SqlBackupType.Full, CopyOnly = false } },
                Processing = { Encrypt = true },
                Destinations = [Destination("Local", DestinationKind.LocalFolder)],
                Retention = { KeepLast = 0, KeepDays = 35 },
                RestoreDrill = { Enabled = true, EveryDays = 7 },
            }),
        T(Databases, "SQL Server point-in-time 2/3: differential (daily)",
            "Differential backup every day at 01:30 (changes since the last full backup).",
            () => new BackupJob
            {
                Name = "SQL Server differential",
                Schedule = { Kind = ScheduleKind.Daily, TimeOfDay = At(1, 30) },
                Source = { Kind = SourceKind.SqlServer, SqlServer = { BackupType = SqlBackupType.Differential, CopyOnly = false } },
                Processing = { Encrypt = true },
                Destinations = [Destination("Local", DestinationKind.LocalFolder)],
                Retention = { KeepLast = 0, KeepDays = 35 },
            }),
        T(Databases, "SQL Server point-in-time 3/3: log (hourly)",
            "Transaction-log backup every hour (FULL recovery model). Use together with the full and differential jobs.",
            () => new BackupJob
            {
                Name = "SQL Server log",
                Schedule = { Kind = ScheduleKind.Cron, CronExpression = "0 * * * *" },
                Source = { Kind = SourceKind.SqlServer, SqlServer = { BackupType = SqlBackupType.Log, CopyOnly = false } },
                Processing = { Encrypt = true },
                Destinations = [Destination("Local", DestinationKind.LocalFolder)],
                Retention = { KeepLast = 0, KeepDays = 14 },
                Notifications = { OnFailure = true, AlertIfNoSuccessForHours = 3 },
            }),
        T(Databases, "PostgreSQL nightly",
            "pg_dump of every database (or the ones you list) every night at 01:30, zstd-compressed and encrypted.",
            () => new BackupJob
            {
                Name = "PostgreSQL",
                Schedule = { Kind = ScheduleKind.Daily, TimeOfDay = At(1, 30) },
                Source = { Kind = SourceKind.PostgreSql },
                Processing = { Encrypt = true, Compression = ArchiveCompression.Zstd },
                Destinations = [Destination("S3", DestinationKind.S3)],
                Retention = BatchJobs.RetentionFor(RetentionPreset.Standard),
                Notifications = { OnFailure = true, AlertIfNoSuccessForHours = 36 },
            }),
        T(Databases, "MySQL / MariaDB nightly",
            "Consistent mysqldump (single transaction) every night at 01:15, zstd-compressed and encrypted.",
            () => new BackupJob
            {
                Name = "MySQL",
                Schedule = { Kind = ScheduleKind.Daily, TimeOfDay = At(1, 15) },
                Source = { Kind = SourceKind.MySql, MySql = { SingleTransaction = true } },
                Processing = { Encrypt = true, Compression = ArchiveCompression.Zstd },
                Destinations = [Destination("S3", DestinationKind.S3)],
                Retention = BatchJobs.RetentionFor(RetentionPreset.Standard),
                Notifications = { OnFailure = true, AlertIfNoSuccessForHours = 36 },
            }),
        T(Databases, "Redis snapshot every 6 hours",
            "RDB snapshot (redis-cli --rdb) every 6 hours. Keeps two days.",
            () => new BackupJob
            {
                Name = "Redis",
                Schedule = { Kind = ScheduleKind.Cron, CronExpression = "0 */6 * * *" },
                Source = { Kind = SourceKind.Redis },
                Processing = { Compression = ArchiveCompression.Zstd },
                Destinations = [Destination("Local", DestinationKind.LocalFolder)],
                Retention = { KeepLast = 8 },
            }),
        T(Databases, "SQLite application database hourly",
            "SQLite files copied with the online backup API every hour, safe while the application is running.",
            () => new BackupJob
            {
                Name = "SQLite",
                Schedule = { Kind = ScheduleKind.Cron, CronExpression = "5 * * * *" },
                Source = { Kind = SourceKind.Sqlite, Sqlite = { DatabasePaths = @"C:\apps\myapp\data.db" } },
                Destinations = [Destination("Local", DestinationKind.LocalFolder)],
                Retention = { KeepLast = 0, KeepDays = 3, KeepDaily = 14 },
            }),

        // ------------------------------------------------------------------ Files
        T(Files, "Documents daily to NAS",
            "User documents every day at 20:00 to a network share (UNC), using shadow copies for open files.",
            () => new BackupJob
            {
                Name = "Documents",
                Schedule = { Kind = ScheduleKind.Daily, TimeOfDay = At(20) },
                Source = { Files = { Paths = [@"%USERPROFILE%\Documents"], UseVss = true } },
                Destinations = [new DestinationDefinition { Name = "NAS", Kind = DestinationKind.LocalFolder, LocalFolder = { Path = @"\\nas\backups" } }],
                Retention = { KeepLast = 7, KeepWeekly = 4, KeepMonthly = 6 },
            }),
        T(Files, "File server share every 2 hours (incremental)",
            "Company share: changed files every 2 hours during the day, a full backup every Sunday, open files from shadow copies. Keeps 30 days plus monthly backups for a year.",
            () => new BackupJob
            {
                Name = "File server",
                Schedule = { Kind = ScheduleKind.Cron, CronExpression = "0 7-21/2 * * 1-6" },
                Source = { Files = { Paths = [@"D:\Shares"], ExcludePatterns = ["~$*", "*.tmp", "Thumbs.db"], UseVss = true, Incremental = true, FullBackupEveryDays = 7 } },
                Processing = { Encrypt = true },
                Destinations = [new DestinationDefinition { Name = "NAS", LocalFolder = { Path = @"\\nas\backups\fileserver" } }],
                Retention = { KeepLast = 0, KeepDays = 30, KeepMonthly = 12 },
            }),
        T(Files, "Large folders deduplicated to S3",
            "Big, slowly changing data (archives, VM images, media): only new chunks are uploaded every night, so daily backups cost little space.",
            () => new BackupJob
            {
                Name = "Archive (deduplicated)",
                Schedule = { Kind = ScheduleKind.Daily, TimeOfDay = At(0, 30) },
                Source = { Files = { Paths = [@"D:\Archive"], UseVss = true } },
                Processing = { Deduplicate = true, Encrypt = true },
                Destinations = [Destination("S3", DestinationKind.S3)],
                Retention = BatchJobs.RetentionFor(RetentionPreset.Long),
            }),
        T(Files, "Photos and media weekly to OneDrive",
            "Pictures and videos every Sunday at 03:00 to OneDrive, without compression (media is already compressed).",
            () => new BackupJob
            {
                Name = "Photos",
                Schedule = { Kind = ScheduleKind.Weekly, TimeOfDay = At(3), DaysOfWeek = [DayOfWeek.Sunday] },
                Source = { Files = { Paths = [@"%USERPROFILE%\Pictures", @"%USERPROFILE%\Videos"] } },
                Processing = { Compression = ArchiveCompression.None, Encrypt = true },
                Destinations = [Destination("OneDrive", DestinationKind.OneDrive)],
                Retention = { KeepLast = 4, KeepMonthly = 12 },
            }),

        // ------------------------------------------------------------------ Server
        T(Server, "Windows server configuration weekly",
            "IIS configuration, scheduled tasks, certificates and chosen registry keys every Saturday at 04:00: what you need to rebuild the server.",
            () => new BackupJob
            {
                Name = "Server configuration",
                Schedule = { Kind = ScheduleKind.Weekly, TimeOfDay = At(4), DaysOfWeek = [DayOfWeek.Saturday] },
                Source = { Kind = SourceKind.WindowsSystem },
                Processing = { Encrypt = true },
                Destinations = [Destination("Google Drive", DestinationKind.GoogleDrive)],
                Retention = { KeepLast = 8, KeepMonthly = 12 },
            }),
        T(Server, "Docker volumes nightly",
            "Named Docker volumes archived every night at 03:30 (optionally stopping containers for consistency).",
            () => new BackupJob
            {
                Name = "Docker volumes",
                Schedule = { Kind = ScheduleKind.Daily, TimeOfDay = At(3, 30) },
                Source = { Kind = SourceKind.DockerVolumes },
                Processing = { Encrypt = true, Compression = ArchiveCompression.Zstd },
                Destinations = [Destination("S3", DestinationKind.S3)],
                Retention = BatchJobs.RetentionFor(RetentionPreset.Standard),
            }),
        T(Server, "Hyper-V virtual machines weekly",
            "Export of the chosen virtual machines every Saturday at 01:00 (running VMs from a production checkpoint), deduplicated so unchanged disk blocks are not uploaded again.",
            () => new BackupJob
            {
                Name = "Hyper-V",
                Schedule = { Kind = ScheduleKind.Weekly, TimeOfDay = At(1), DaysOfWeek = [DayOfWeek.Saturday] },
                Source = { Kind = SourceKind.HyperV },
                Processing = { Deduplicate = true, Encrypt = true },
                Destinations = [new DestinationDefinition { Name = "NAS", LocalFolder = { Path = @"\\nas\backups\hyperv" } }],
                Retention = { KeepLast = 4 },
            }),

        // ------------------------------------------------------------------ Off-site
        T(Offsite, "Off-site copy of another job (3-2-1)",
            "Copies the backups of another job, still encrypted, to a second destination every night at 05:00. Set the job to copy on the Source tab.",
            () => new BackupJob
            {
                Name = "Off-site copy",
                Schedule = { Kind = ScheduleKind.Daily, TimeOfDay = At(5) },
                Source = { Kind = SourceKind.CopyOf },
                Destinations = [Destination("S3", DestinationKind.S3)],
                Retention = BatchJobs.RetentionFor(RetentionPreset.Long),
            }),
        T(Offsite, "Website to NAS + Telegram archive",
            "Website every night at 02:00 to a network share (for restores) and to a private Telegram channel as a disaster copy.",
            () => new BackupJob
            {
                Name = "Website (NAS + Telegram)",
                Schedule = { Kind = ScheduleKind.Daily, TimeOfDay = At(2) },
                Source = { Files = { Paths = [@"C:\inetpub\wwwroot"], ExcludePatterns = [.. WebExcludes], UseVss = true } },
                Processing = { Encrypt = true, Compression = ArchiveCompression.Zstd },
                Destinations =
                [
                    new DestinationDefinition { Name = "NAS", LocalFolder = { Path = @"\\nas\backups" } },
                    Destination("Telegram", DestinationKind.Telegram),
                ],
                Retention = BatchJobs.RetentionFor(RetentionPreset.Standard),
            }),
    ];
}
