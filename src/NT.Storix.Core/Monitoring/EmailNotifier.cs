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
    public async Task NotifyAsync(Notification notification, CancellationToken cancellationToken)
    {
        var recipients = notification.Event == NotificationEvent.Summary
            ? settingsRepository.Get().WeeklySummary.Recipients
            : notification.Job?.Notifications.EmailTo;
        if (string.IsNullOrWhiteSpace(recipients))
        {
            return;
        }

        var smtp = settingsRepository.Get().Smtp;
        if (!smtp.Enabled || string.IsNullOrWhiteSpace(smtp.Host) || string.IsNullOrWhiteSpace(smtp.From))
        {
            logger.LogWarning("E-mail notification for job {Job} skipped: SMTP is not configured.", notification.Job?.Name);
            return;
        }

        try
        {
            await SendAsync(smtp, recipients, $"[Storix] {notification.Title}", BuildBody(notification), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError("Failed to send e-mail notification for job {Job}: {Error}", notification.Job?.Name, ex.Message);
        }
    }

    public static async Task SendAsync(SmtpSettings smtp, string recipients, string subject, string body, CancellationToken cancellationToken)
    {
        using var message = new MailMessage { From = new MailAddress(smtp.From), Subject = subject, Body = body };
        foreach (var address in recipients.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
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

    private static string BuildBody(Notification notification)
    {
        var body = new StringBuilder().AppendLine(notification.Text);
        if (notification.Run is { } run)
        {
            if (run.Sha256 is not null)
            {
                body.AppendLine($"SHA-256: {run.Sha256}");
            }

            if (!string.IsNullOrWhiteSpace(run.Log))
            {
                body.AppendLine().AppendLine("Log:").AppendLine(run.Log);
            }
        }

        return body.ToString();
    }
}
