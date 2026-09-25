namespace NT.Storix.Core.Monitoring;

/// <summary>Decides when to raise a "no successful backup for too long" alert.</summary>
public static class DeadMansSwitch
{
    /// <param name="thresholdHours">Alert threshold; 0 or less disables the check.</param>
    /// <param name="lastSuccess">Last successful run, if any.</param>
    /// <param name="firstRun">First recorded run (used when the job never succeeded).</param>
    /// <param name="lastAlert">When the previous alert was sent; alerts repeat once per threshold period.</param>
    public static bool ShouldAlert(int thresholdHours, DateTimeOffset? lastSuccess, DateTimeOffset? firstRun, DateTimeOffset? lastAlert, DateTimeOffset now)
    {
        if (thresholdHours <= 0)
        {
            return false;
        }

        var reference = lastSuccess ?? firstRun;
        if (reference is null)
        {
            return false; // The job never ran: nothing to compare with yet.
        }

        var threshold = TimeSpan.FromHours(thresholdHours);
        if (now - reference.Value < threshold)
        {
            return false;
        }

        return lastAlert is null || lastAlert.Value < reference.Value || now - lastAlert.Value >= threshold;
    }
}
