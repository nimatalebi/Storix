using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NT.Storix.Core.Engine;
using NT.Storix.Core.Models;
using NT.Storix.Core.Monitoring;
using NT.Storix.Core.Persistence;
using NT.Storix.Core.Security;
using NT.Storix.Core.Sources;
using NT.Storix.Core.Destinations;

namespace NT.Storix.Core.Tests;

public class NotificationTests
{
    private sealed class PlainProtector : ISecretProtector
    {
        public string? Protect(string? plainText) => plainText;

        public string? Unprotect(string? protectedText) => protectedText;
    }

    /// <summary>Records requests instead of sending them.</summary>
    private sealed class RecordingHandler(HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request, body));
            return new HttpResponseMessage(status);
        }
    }

    private sealed class RecordingNotifier : INotifier
    {
        public List<Notification> Sent { get; } = [];

        public Task NotifyAsync(Notification notification, CancellationToken cancellationToken)
        {
            Sent.Add(notification);
            return Task.CompletedTask;
        }
    }

    private static Notification SampleFailure()
    {
        var job = new BackupJob { Name = "Nightly <SQL>" };
        var run = new BackupRun { JobId = job.Id, JobName = job.Name, Status = RunStatus.Failed, Message = "Disk full", FinishedAt = DateTimeOffset.UtcNow };
        return Notification.ForRun(job, run);
    }

    [Fact]
    public void Webhook_payload_is_json_and_signed()
    {
        var channel = new NotificationChannel { Kind = NotificationChannelKind.Webhook, Url = "https://example.com/hook", SigningSecret = "s3cret" };
        using var request = ChannelNotifier.BuildRequest(channel, SampleFailure());
        var body = request.Content!.ReadAsStringAsync().Result;

        using var json = JsonDocument.Parse(body);
        Assert.Equal("failure", json.RootElement.GetProperty("event").GetString());
        Assert.Equal("Failed", json.RootElement.GetProperty("run").GetProperty("status").GetString());
        Assert.Equal("Disk full", json.RootElement.GetProperty("run").GetProperty("message").GetString());
        Assert.Equal("sha256=" + ChannelNotifier.Sign(body, "s3cret"), request.Headers.GetValues(ChannelNotifier.SignatureHeader).Single());
    }

    [Theory]
    [InlineData(NotificationChannelKind.Telegram, "https://api.telegram.org/bot123:ABC/sendMessage")]
    [InlineData(NotificationChannelKind.Bale, "https://tapi.bale.ai/bot123:ABC/sendMessage")]
    public void Telegram_and_bale_use_bot_api_with_escaped_html(NotificationChannelKind kind, string expectedUrl)
    {
        var channel = new NotificationChannel { Kind = kind, BotToken = "123:ABC", ChatId = "@ops" };
        using var request = ChannelNotifier.BuildRequest(channel, SampleFailure());
        var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().Result).RootElement;

        Assert.Equal(expectedUrl, request.RequestUri!.ToString());
        Assert.Equal("@ops", body.GetProperty("chat_id").GetString());
        Assert.Contains("&lt;SQL&gt;", body.GetProperty("text").GetString());
        Assert.Equal("HTML", body.GetProperty("parse_mode").GetString());
    }

    [Theory]
    [InlineData(NotificationChannelKind.Slack, "text")]
    [InlineData(NotificationChannelKind.Teams, "text")]
    [InlineData(NotificationChannelKind.Discord, "content")]
    public void Chat_webhooks_use_their_text_field(NotificationChannelKind kind, string field)
    {
        var channel = new NotificationChannel { Kind = kind, Url = "https://hooks.example.com/x" };
        using var request = ChannelNotifier.BuildRequest(channel, SampleFailure());
        var text = JsonDocument.Parse(request.Content!.ReadAsStringAsync().Result).RootElement.GetProperty(field).GetString();
        Assert.Contains("Disk full", text);
    }

    [Fact]
    public async Task Only_failures_channels_skip_successes_and_errors_are_not_thrown()
    {
        using var temp = new TempDirectory();
        var settings = new SettingsRepository(new StorixDatabase(temp.Combine("s.db")), new PlainProtector());
        settings.Save(new AppSettings
        {
            Channels =
            [
                new NotificationChannel { Name = "all", Url = "https://a.example/hook" },
                new NotificationChannel { Name = "failures", Url = "https://b.example/hook", OnlyFailures = true },
                new NotificationChannel { Name = "off", Url = "https://c.example/hook", Enabled = false },
            ],
        });
        var handler = new RecordingHandler(HttpStatusCode.InternalServerError);
        var notifier = new ChannelNotifier(settings, new HttpClient(handler), NullLogger<ChannelNotifier>.Instance);

        var job = new BackupJob { Name = "J" };
        await notifier.NotifyAsync(Notification.ForRun(job, new BackupRun { JobName = "J", Status = RunStatus.Succeeded }), CancellationToken.None);
        Assert.Equal(["a.example"], handler.Requests.Select(r => r.Request.RequestUri!.Host));

        handler.Requests.Clear();
        await notifier.NotifyAsync(Notification.ForStale(job, null), CancellationToken.None);
        Assert.Equal(["a.example", "b.example"], handler.Requests.Select(r => r.Request.RequestUri!.Host));
    }

    [Theory]
    [InlineData("https://hc-ping.com/abc", HealthCheckSignal.Start, "https://hc-ping.com/abc/start")]
    [InlineData("https://hc-ping.com/abc/", HealthCheckSignal.Success, "https://hc-ping.com/abc/")]
    [InlineData("https://hc-ping.com/abc", HealthCheckSignal.Failure, "https://hc-ping.com/abc/fail")]
    [InlineData("https://kuma.local/api/push/X?status={status}&msg={message}", HealthCheckSignal.Failure, "https://kuma.local/api/push/X?status=down&msg=disk%20full")]
    [InlineData("https://kuma.local/api/push/X?status={status}", HealthCheckSignal.Start, null)]
    [InlineData("", HealthCheckSignal.Success, null)]
    public void Health_check_urls(string template, HealthCheckSignal signal, string? expected)
    {
        Assert.Equal(expected, HealthCheckPinger.BuildUrl(template, signal, "disk full"));
    }

    [Fact]
    public void Dead_mans_switch_rules()
    {
        var now = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

        Assert.False(DeadMansSwitch.ShouldAlert(0, now.AddDays(-10), null, null, now));
        Assert.False(DeadMansSwitch.ShouldAlert(24, null, null, null, now));
        Assert.False(DeadMansSwitch.ShouldAlert(24, now.AddHours(-23), null, null, now));
        Assert.True(DeadMansSwitch.ShouldAlert(24, now.AddHours(-25), null, null, now));
        Assert.True(DeadMansSwitch.ShouldAlert(24, null, now.AddHours(-30), null, now)); // Never succeeded.
        Assert.False(DeadMansSwitch.ShouldAlert(24, now.AddHours(-30), null, now.AddHours(-2), now)); // Already alerted.
        Assert.True(DeadMansSwitch.ShouldAlert(24, now.AddHours(-60), null, now.AddHours(-25), now)); // Repeat after a period.
    }

    [Fact]
    public async Task Scheduler_sends_stale_alert_once_per_period()
    {
        using var temp = new TempDirectory();
        var database = new StorixDatabase(temp.Combine("s.db"));
        var settings = new SettingsRepository(database, new PlainProtector());
        var jobs = new JobRepository(database, new PlainProtector());
        var runs = new RunRepository(database);
        var recorder = new RecordingNotifier();

        var job = new BackupJob { Name = "Watched", Notifications = { AlertIfNoSuccessForHours = 12 } };
        jobs.Save(job);
        var old = new BackupRun { JobId = job.Id, JobName = job.Name, StartedAt = DateTimeOffset.UtcNow.AddDays(-2) };
        runs.Insert(old);
        old.Status = RunStatus.Succeeded;
        runs.Complete(old);

        var runner = new BackupJobRunner(runs, settings, new SourceFactory(), new DestinationFactory(), [recorder], NullLogger<BackupJobRunner>.Instance);
        var scheduler = new BackupScheduler(jobs, runs, settings, runner, [recorder], NullLogger<BackupScheduler>.Instance);

        Assert.Equal(1, await scheduler.CheckStaleJobsAsync(DateTimeOffset.UtcNow));
        Assert.Equal(0, await scheduler.CheckStaleJobsAsync(DateTimeOffset.UtcNow.AddHours(1)));
        Assert.Equal(NotificationEvent.Stale, Assert.Single(recorder.Sent).Event);
    }

    [Fact]
    public async Task Runner_pings_health_check_and_respects_notification_flags()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("src/a.txt", "a");
        var database = new StorixDatabase(temp.Combine("s.db"));
        var settings = new SettingsRepository(database, new PlainProtector());
        settings.Save(new AppSettings { StagingDirectory = temp.Combine("staging") });
        var handler = new RecordingHandler();
        var recorder = new RecordingNotifier();
        var runner = new BackupJobRunner(new RunRepository(database), settings, new SourceFactory(), new DestinationFactory(), [recorder], NullLogger<BackupJobRunner>.Instance, new HttpClient(handler));

        var job = new BackupJob
        {
            Name = "Pinged",
            Source = { Files = { Paths = [temp.Combine("src")] } },
            Destinations = [new DestinationDefinition { LocalFolder = { Path = temp.Combine("dst") } }],
            Notifications = { HealthCheckUrl = "https://hc-ping.com/uuid", OnSuccess = false, OnFailure = true },
        };

        var run = await runner.RunAsync(job, RunTrigger.Manual, CancellationToken.None);

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal(["https://hc-ping.com/uuid/start", "https://hc-ping.com/uuid"], handler.Requests.Select(r => r.Request.RequestUri!.ToString()));
        Assert.Empty(recorder.Sent); // OnSuccess = false.
    }
}
