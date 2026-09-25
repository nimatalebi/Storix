using System.Net;
using System.Text;
using NT.Storix.Core.Engine;
using NT.Storix.Core.Monitoring;
using NT.Storix.Core.Persistence;

namespace NT.Storix.Service;

/// <summary>Serves Prometheus metrics at /metrics when enabled in the settings.</summary>
public sealed class MetricsServer(
    SettingsRepository settings,
    JobRepository jobs,
    RunRepository runs,
    BackupScheduler scheduler,
    ILogger<MetricsServer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = settings.Get().Observability;
        if (!options.MetricsEnabled)
        {
            return;
        }

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://{(options.MetricsRemoteAccess ? "+" : "localhost")}:{options.MetricsPort}/");
        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            logger.LogError("Could not start the metrics endpoint on port {Port}: {Error}", options.MetricsPort, ex.Message);
            return;
        }

        logger.LogInformation("Prometheus metrics available on port {Port} at /metrics.", options.MetricsPort);
        await using var registration = stoppingToken.Register(listener.Stop);
        while (!stoppingToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                if (context.Request.Url?.AbsolutePath != "/metrics")
                {
                    context.Response.StatusCode = 404;
                    continue;
                }

                var body = Encoding.UTF8.GetBytes(PrometheusExporter.Render(jobs.GetAll(), runs.GetRecent(null, 10_000), scheduler.RunningJobs));
                context.Response.ContentType = "text/plain; version=0.0.4; charset=utf-8";
                await context.Response.OutputStream.WriteAsync(body, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Metrics request failed: {Error}", ex.Message);
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }
    }
}
