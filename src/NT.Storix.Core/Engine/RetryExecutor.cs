using NT.Storix.Core.Models;

namespace NT.Storix.Core.Engine;

/// <summary>Executes an operation with exponential back-off according to a <see cref="RetryPolicy"/>.</summary>
public static class RetryExecutor
{
    public static async Task<T> ExecuteAsync<T>(
        RetryPolicy policy,
        string operationName,
        Func<int, CancellationToken, Task<T>> operation,
        RunLog? log,
        CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;
        var attempts = Math.Max(1, policy.MaxAttempts);
        var wait = TimeSpan.FromSeconds(Math.Max(0, policy.InitialDelaySeconds));

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation(attempt, cancellationToken);
            }
            catch (Exception ex) when (attempt < attempts && ex is not OperationCanceledException)
            {
                log?.Warn($"{operationName} failed (attempt {attempt}/{attempts}): {ex.Message}. Retrying in {wait.TotalSeconds:0}s.");
                await delay(wait, cancellationToken);
                wait = TimeSpan.FromSeconds(wait.TotalSeconds * Math.Max(1, policy.BackoffMultiplier));
            }
        }
    }

    public static Task ExecuteAsync(
        RetryPolicy policy,
        string operationName,
        Func<int, CancellationToken, Task> operation,
        RunLog? log,
        CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        ExecuteAsync(policy, operationName, async (attempt, ct) =>
        {
            await operation(attempt, ct);
            return true;
        }, log, cancellationToken, delay);
}
