using NT.Storix.Core.Configuration;
using NT.Storix.Core.Engine;
using NT.Storix.Core.Models;
using NT.Storix.Core.Scheduling;

namespace NT.Storix.Core.Tests;

public class TemplateTests
{
    [Fact]
    public void Templates_are_many_distinct_and_consistent()
    {
        Assert.True(JobTemplate.All.Count >= 20);
        Assert.Equal(JobTemplate.All.Count, JobTemplate.All.Select(t => t.Name).Distinct().Count());
        foreach (var template in JobTemplate.All)
        {
            var job = template.Create();
            Assert.Null(ScheduleCalculator.Validate(job.Schedule));
            Assert.NotEmpty(job.Destinations);
            Assert.Contains(template.Category, new[] { JobTemplate.Websites, JobTemplate.Databases, JobTemplate.Files, JobTemplate.Server, JobTemplate.Offsite });

            // Only things the user must fill in (credentials, databases...) may be missing, never a contradiction.
            var errors = BackupJobRunner.GetValidationErrors(job);
            Assert.DoesNotContain(errors, e => e.Contains("Schedule") || e.Contains("archive-only") || e.Contains("already incremental") || e.Contains("public key"));
            Assert.NotSame(job, template.Create());
            Assert.NotEqual(template.Name, template.NameFor(persian: true));
            Assert.NotEqual(template.Description, template.DescriptionFor(persian: true));
        }
    }

    [Fact]
    public void Batch_creates_one_job_per_item_with_staggered_times_and_shared_destination()
    {
        var drive = new DestinationDefinition { Name = "Google Drive", Kind = DestinationKind.GoogleDrive };
        var options = new BatchOptions
        {
            Kind = BatchKind.MongoDbDatabases,
            ConnectionString = "mongodb://localhost:27017",
            Items = [new("shop", "shop"), new("blog", "blog"), new("crm", "crm")],
            Destinations = [drive],
            FirstStart = new TimeSpan(1, 50, 0),
            StaggerMinutes = 20,
            EncryptionPassword = "pw",
            RecoveryInfoConfirmed = true,
        };

        var jobs = BatchJobs.Create(options);

        Assert.Equal(["MongoDB - shop", "MongoDB - blog", "MongoDB - crm"], jobs.Select(j => j.Name));
        Assert.Equal([new TimeSpan(1, 50, 0), new TimeSpan(2, 10, 0), new TimeSpan(2, 30, 0)], jobs.Select(j => j.Schedule.TimeOfDay));
        Assert.All(jobs, j =>
        {
            Assert.Equal(drive.Id, Assert.Single(j.Destinations).Id);
            Assert.NotSame(drive, j.Destinations[0]);
            Assert.True(j.Processing.Encrypt);
            Assert.Equal(SourceKind.MongoDb, j.Source.Kind);
            Assert.Empty(BackupJobRunner.GetValidationErrors(j));
        });
        Assert.Equal("blog", jobs[1].Source.MongoDb.Database);
        Assert.Equal(7, jobs[0].Retention.KeepDaily);
    }

    [Fact]
    public void Website_batch_excludes_logs_and_caches_and_wraps_past_midnight()
    {
        var jobs = BatchJobs.Create(new BatchOptions
        {
            Kind = BatchKind.Websites,
            Items = [new("shop.example.com", @"C:\inetpub\shop"), new("blog", @"C:\inetpub\blog")],
            Destinations = [new DestinationDefinition { Name = "NAS" }],
            FirstStart = new TimeSpan(23, 50, 0),
            StaggerMinutes = 15,
            Schedule = ScheduleKind.Weekly,
            WeeklyDay = DayOfWeek.Sunday,
        });

        Assert.Equal("Website - shop.example.com", jobs[0].Name);
        Assert.Contains("node_modules", jobs[0].Source.Files.ExcludePatterns);
        Assert.Equal(new TimeSpan(0, 5, 0), jobs[1].Schedule.TimeOfDay);
        Assert.Equal([DayOfWeek.Sunday], jobs[1].Schedule.DaysOfWeek);
        Assert.False(jobs[0].Processing.Encrypt);
        Assert.Throws<ArgumentException>(() => BatchJobs.Create(new BatchOptions()));
    }

    [Fact]
    public void Iis_sites_are_read_from_application_host_config()
    {
        const string xml = """
            <configuration><system.applicationHost><sites>
              <site name="Default Web Site" id="1">
                <application path="/"><virtualDirectory path="/" physicalPath="%SystemDrive%\inetpub\wwwroot" /></application>
              </site>
              <site name="shop" id="2">
                <application path="/api"><virtualDirectory path="/" physicalPath="D:\sites\shop-api" /></application>
                <application path="/"><virtualDirectory path="/" physicalPath="D:\sites\shop" /></application>
              </site>
              <siteDefaults />
            </sites></system.applicationHost></configuration>
            """;

        var sites = ServerDiscovery.ParseIisSites(xml);

        Assert.Equal(["Default Web Site", "shop"], sites.Select(s => s.Name));
        Assert.Equal(@"D:\sites\shop", sites[1].PhysicalPath);
        if (OperatingSystem.IsWindows())
        {
            Assert.DoesNotContain("%SystemDrive%", sites[0].PhysicalPath);
        }
    }
}
