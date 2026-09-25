namespace NT.Storix.Core.Models;

/// <summary>Commands executed before and after a backup (e.g. stop an IIS app pool, flush a cache).</summary>
public sealed class JobHooks
{
    /// <summary>Command line run before the backup (cmd.exe on Windows, /bin/sh elsewhere).</summary>
    public string? PreCommand { get; set; }

    /// <summary>Command line run after the backup, also when it failed. STORIX_STATUS holds the result.</summary>
    public string? PostCommand { get; set; }

    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>When the pre-command fails (non-zero exit code or timeout), fail the job instead of continuing.</summary>
    public bool AbortOnPreCommandFailure { get; set; } = true;
}
