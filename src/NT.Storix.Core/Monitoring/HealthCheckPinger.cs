namespace NT.Storix.Core.Monitoring;

public enum HealthCheckSignal
{
    Start,
    Success,
    Failure,
}

/// <summary>Pings external monitoring (healthchecks.io, Uptime Kuma push monitors, cron monitors).</summary>
public static class HealthCheckPinger
{
    /// <summary>Builds the URL for a signal, or returns null when nothing should be sent.</summary>
    public static string? BuildUrl(string? template, HealthCheckSignal signal, string? message = null)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return null;
        }

        var url = template.Trim();
        if (url.Contains("{status}", StringComparison.OrdinalIgnoreCase))
        {
            // Uptime Kuma style: one URL with a status parameter; "start" is not reported.
            if (signal == HealthCheckSignal.Start)
            {
                return null;
            }

            return url.Replace("{status}", signal == HealthCheckSignal.Success ? "up" : "down", StringComparison.OrdinalIgnoreCase)
                      .Replace("{message}", Uri.EscapeDataString(Truncate(message ?? string.Empty, 200)), StringComparison.OrdinalIgnoreCase);
        }

        // healthchecks.io style.
        return signal switch
        {
            HealthCheckSignal.Start => url.TrimEnd('/') + "/start",
            HealthCheckSignal.Failure => url.TrimEnd('/') + "/fail",
            _ => url,
        };
    }

    public static async Task PingAsync(HttpClient http, string? template, HealthCheckSignal signal, string? message, CancellationToken cancellationToken)
    {
        if (BuildUrl(template, signal, message) is not { } url)
        {
            return;
        }

        using var content = new StringContent(Truncate(message ?? string.Empty, 10_000));
        using var response = await http.PostAsync(url, content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}
