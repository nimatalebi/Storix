using System.Net;
using System.Net.Mail;
using System.Text;
using Microsoft.Extensions.Logging;
using NT.Storix.Core.Models;
using NT.Storix.Core.Persistence;

namespace NT.Storix.Core.Monitoring;

/// <summary>Sends a short e-mail report after a run, based on the job's notification options.</summary>
public sealed class EmailNotifier(SettingsRepository settingsRepository, ILogger<EmailNotifier> logger) : INotifier
{
    public async Task NotifyAsync(BackupJob job, BackupRun run, CancellationToken cancellationToken)
    {
        var success = run.Status == RunStatus.Succeeded;
        var wanted = success ? job.Notifications.OnSuccess : job.Notifications.OnFailure;
        if (!wanted || string.IsNullOrWhiteSpace(job.Notifications.EmailTo))
        {
            return;
        }

        var smtp = settingsRepository.Get().Smtp;
        if (!smtp.Enabled || string.IsNullOrWhiteSpace(smtp.Host) || string.IsNullOrWhiteSpace(smtp.From))
        {
            logger.LogWarning("E-mail notification for job {Job} skipped: SMTP is not configured.", job.Name);
            return;
        }

        try
        {
            using var message = new MailMessage
            {
                From = new MailAddress(smtp.From),
                Subject = $"[Storix] {job.Name}: {run.Status}",
                Body = BuildBody(run),
            };

            foreach (var address in job.Notifications.EmailTo.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                message.To.Add(address);
            }

            using var client = new SmtpClient(smtp.Host, smtp.Port) { EnableSsl = smtp.UseSsl };
            if (!string.IsNullOrWhiteSpace(smtp.UserName))
            {
                client.Credentials = new NetworkCredential(smtp.UserName, smtp.Password);
            }

            await client.SendMailAsync(message, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to send e-mail notification for job {Job}.", job.Name);
        }
    }

    private static string BuildBody(BackupRun run)
    {
        var body = new StringBuilder()
            .AppendLine($"Job:       {run.JobName}")
            .AppendLine($"Status:    {run.Status}")
            .AppendLine($"Machine:   {Environment.MachineName}")
            .AppendLine($"Started:   {run.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}")
            .AppendLine($"Duration:  {run.Duration:hh\\:mm\\:ss}");

        if (run.FileName is not null)
        {
            body.AppendLine($"File:      {run.FileName}")
                .AppendLine($"Size:      {run.SizeBytes:N0} bytes")
                .AppendLine($"SHA-256:   {run.Sha256}");
        }

        if (!string.IsNullOrWhiteSpace(run.Message))
        {
            body.AppendLine().AppendLine(run.Message);
        }

        if (!string.IsNullOrWhiteSpace(run.Log))
        {
            body.AppendLine().AppendLine("Log:").AppendLine(run.Log);
        }

        return body.ToString();
    }
}
