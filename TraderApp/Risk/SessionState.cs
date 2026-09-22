namespace TraderApp.Risk;

/// <summary>
/// Snapshot of the risk counters that must survive a process restart.
/// Without this, Railway's ALWAYS restart policy wiped a hard halt and
/// started a fresh session with loss streak 0.
/// </summary>
public sealed class SessionState
{
    public bool Halted { get; set; }
    public string HaltReason { get; set; } = "";
    public string HaltKind { get; set; } = "";
    public DateTime? HaltedAtUtc { get; set; }
    public int ConsecutiveLosses { get; set; }
    public int EntriesToday { get; set; }
    public string EntriesDay { get; set; } = "";
    public decimal SessionStartEquity { get; set; }
    public decimal PeakEquity { get; set; }
    public DateTime? CooldownUntilUtc { get; set; }

    /// <summary>UTC day (yyyy-MM-dd) on which the profit lock switched to reduced risk. Empty = not locked.</summary>
    public string ProfitLockedDay { get; set; } = "";

    /// <summary>
    /// Manual pause from /pause. Persisted deliberately: otherwise a redeploy
    /// would silently resume trading on a bot someone had paused on purpose.
    /// Unlike the day-scoped locks it never clears itself - only /resume does.
    /// </summary>
    public bool EntriesPaused { get; set; }
    public DateTime? PausedAtUtc { get; set; }
}
