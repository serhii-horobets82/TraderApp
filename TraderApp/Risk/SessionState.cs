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
    public DateTime? HaltedAtUtc { get; set; }
    public int ConsecutiveLosses { get; set; }
    public int EntriesToday { get; set; }
    public string EntriesDay { get; set; } = "";
    public decimal SessionStartEquity { get; set; }
    public decimal PeakEquity { get; set; }
    public DateTime? CooldownUntilUtc { get; set; }
}
