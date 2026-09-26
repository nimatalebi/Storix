using NT.Storix.Core.Models;
using NT.Storix.Core.Monitoring;

namespace NT.Storix.Core.Configuration;

/// <summary>Short facts about a job for template cards: when, what, where and how it is protected.</summary>
public static class TemplateTags
{
    public static IReadOnlyList<string> For(BackupJob job, bool persian)
    {
        var tags = new List<string> { When(job.Schedule, persian), What(job.Source, persian) };
        tags.AddRange(job.Destinations.Select(d => Where(d.Kind, persian)).Distinct());
        if (job.Processing.Encrypt)
        {
            tags.Add(persian ? "رمزنگاری" : "Encrypted");
        }

        if (job.Processing.Deduplicate)
        {
            tags.Add(persian ? "حذف تکرار" : "Deduplicated");
        }
        else if (job.Source.Kind == SourceKind.Files && job.Source.Files.Incremental)
        {
            tags.Add(persian ? "افزایشی" : "Incremental");
        }

        return tags;
    }

    private static string When(ScheduleDefinition schedule, bool persian)
    {
        var time = schedule.TimeOfDay.ToString(@"hh\:mm");
        return schedule.Kind switch
        {
            ScheduleKind.Daily => persian ? $"روزانه {TimeText.Digits(time)}" : $"Daily {time}",
            ScheduleKind.Weekly => persian ? $"هفتگی {TimeText.Digits(time)}" : $"Weekly {time}",
            ScheduleKind.Cron when schedule.CronExpression?.StartsWith("0 */6", StringComparison.Ordinal) == true => persian ? "هر ۶ ساعت" : "Every 6 h",
            ScheduleKind.Cron when schedule.CronExpression?.Contains("/2", StringComparison.Ordinal) == true => persian ? "هر ۲ ساعت" : "Every 2 h",
            ScheduleKind.Cron => persian ? "ساعتی" : "Hourly",
            _ => persian ? "دستی" : "Manual",
        };
    }

    private static string What(SourceDefinition source, bool persian) => source.Kind switch
    {
        SourceKind.Files => persian ? "فایل‌ها" : "Files",
        SourceKind.SqlServer => "SQL Server",
        SourceKind.MongoDb => "MongoDB",
        SourceKind.PostgreSql => "PostgreSQL",
        SourceKind.MySql => "MySQL",
        SourceKind.Redis => "Redis",
        SourceKind.Sqlite => "SQLite",
        SourceKind.WindowsSystem => persian ? "تنظیمات ویندوز" : "Windows config",
        SourceKind.DockerVolumes => "Docker",
        SourceKind.HyperV => "Hyper-V",
        SourceKind.CopyOf => persian ? "کپی کار دیگر" : "Copy of a job",
        _ => persian ? "افزونه" : "Plugin",
    };

    private static string Where(DestinationKind kind, bool persian) => kind switch
    {
        DestinationKind.LocalFolder => persian ? "پوشه / NAS" : "Folder / NAS",
        DestinationKind.GoogleDrive => persian ? "گوگل‌درایو" : "Google Drive",
        DestinationKind.OneDrive => persian ? "وان‌درایو" : "OneDrive",
        DestinationKind.Dropbox => persian ? "دراپ‌باکس" : "Dropbox",
        DestinationKind.Telegram => persian ? "تلگرام" : "Telegram",
        DestinationKind.AzureBlob => "Azure",
        DestinationKind.WebDav => "WebDAV",
        DestinationKind.Ftp => "FTP",
        DestinationKind.Sftp => "SFTP",
        DestinationKind.Rclone => "rclone",
        _ => kind.ToString(),
    };
}
