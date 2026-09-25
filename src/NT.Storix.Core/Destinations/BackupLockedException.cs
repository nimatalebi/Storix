namespace NT.Storix.Core.Destinations;

/// <summary>A backup cannot be deleted because it is protected by an immutability lock (e.g. S3 Object Lock).</summary>
public sealed class BackupLockedException(string name, DateTimeOffset? lockedUntil)
    : IOException($"'{name}' is locked{(lockedUntil is null ? string.Empty : $" until {lockedUntil.Value.ToLocalTime():yyyy-MM-dd HH:mm}")} and cannot be deleted.")
{
    public DateTimeOffset? LockedUntil { get; } = lockedUntil;
}
