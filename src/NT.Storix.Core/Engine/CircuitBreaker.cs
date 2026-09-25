using System.Collections.Concurrent;

namespace NT.Storix.Core.Engine;

/// <summary>
/// Per-destination circuit breaker: after several consecutive failed runs a destination is skipped for a while,
/// so a dead server does not cost every job its full retry time.
/// </summary>
public sealed class CircuitBreaker(int threshold, TimeSpan cooldown, TimeProvider? time = null)
{
    private readonly ConcurrentDictionary<Guid, (int Failures, DateTimeOffset OpenUntil)> _state = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Process-wide instance used by the service.</summary>
    public static CircuitBreaker Shared { get; } = new(5, TimeSpan.FromMinutes(30));

    /// <summary>Returns the time until which the destination is skipped, or null when it may be used.</summary>
    public DateTimeOffset? OpenUntil(Guid destination) =>
        _state.TryGetValue(destination, out var state) && state.OpenUntil > _time.GetUtcNow() ? state.OpenUntil : null;

    public void RecordSuccess(Guid destination) => _state.TryRemove(destination, out _);

    public void RecordFailure(Guid destination) =>
        _state.AddOrUpdate(
            destination,
            _ => (1, DateTimeOffset.MinValue),
            (_, state) =>
            {
                var failures = state.Failures + 1;
                return failures >= threshold ? (0, _time.GetUtcNow() + cooldown) : (failures, state.OpenUntil);
            });
}
