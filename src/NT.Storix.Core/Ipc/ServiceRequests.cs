using NT.Storix.Core.Persistence;

namespace NT.Storix.Core.Ipc;

/// <summary>Asks the service to run, cancel or drill a job: over the local API when it is reachable, else via the database queue.</summary>
public static class ServiceRequests
{
    public static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <returns>True when the service took the request right away; false when it was queued for its next check.</returns>
    public static async Task<bool> SendAsync(RunRepository runs, string command, Guid jobId, string pipeName = StorixPipe.DefaultName)
    {
        var response = await StorixPipe.SendAsync(new PipeRequest { Command = command, JobId = jobId }, Timeout, pipeName).ConfigureAwait(false);
        if (response is { Ok: true })
        {
            return true;
        }

        switch (command)
        {
            case "run":
                runs.RequestRun(jobId);
                break;
            case "cancel":
                runs.RequestCancel(jobId);
                break;
            case "drill":
                runs.RequestDrill(jobId);
                break;
            default:
                throw new ArgumentException($"Unknown command '{command}'.", nameof(command));
        }

        return false;
    }
}
