using NT.Storix.Core.Engine;
using NT.Storix.Core.Ipc;
using NT.Storix.Core.Persistence;

namespace NT.Storix.Service;

/// <summary>Local API for the manager and the CLI: immediate run/cancel/drill and live status.</summary>
public sealed class PipeServer(BackupScheduler scheduler, RunRepository runs, JobRepository jobs, ILogger<PipeServer> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        StorixPipe.ServeAsync((request, _) => Task.FromResult(Handle(request)), logger, stoppingToken);

    private PipeResponse Handle(PipeRequest request)
    {
        switch (request.Command)
        {
            case "status":
                return new PipeResponse { Ok = true, Running = [.. scheduler.RunningDetails] };
            case "run" or "cancel" or "drill":
                if (request.JobId is not { } id || jobs.Get(id) is null)
                {
                    return new PipeResponse { Error = "Unknown job." };
                }

                // Same queue as without the pipe (durable, one code path); the wake-up only makes it immediate.
                switch (request.Command)
                {
                    case "run":
                        runs.RequestRun(id);
                        break;
                    case "cancel":
                        runs.RequestCancel(id);
                        break;
                    default:
                        runs.RequestDrill(id);
                        break;
                }

                scheduler.Wake();
                return new PipeResponse { Ok = true, Running = [.. scheduler.RunningDetails] };
            default:
                return new PipeResponse { Error = $"Unknown command '{request.Command}'." };
        }
    }
}
