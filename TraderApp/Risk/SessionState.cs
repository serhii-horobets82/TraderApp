namespace TraderApp.Risk;

/// <summary>
/// Snapshot of the risk counters that must survive a process restart.
/// Without this, Railway's ALWAYS restart policy wiped a hard halt and
/// started a fresh session with loss streak 0.
/// </summary>
public sealed class SessionState
{
    /// <summary>
    /// A one-shot ClearHaltOnBoot request has already been honoured for this
    /// persisted state. This prevents a forgotten environment variable from
    /// erasing every later restart's risk counters and hard halts.
    /// </summary>
    public bool ClearHaltOnBootConsumed { get; set; }

    public bool Halted { get; set; }
    public string HaltReason { get; set; } = "";
    public string HaltKind { get; set; } = "";
    public DateTime? HaltedAtUtc { get; set; }
    public int ConsecutiveLosses { get; set; }

    /// <summary>Realised consecutive-loss counters per market.</summary>
    public Dictionary<string, int> ConsecutiveLossesByMarket { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-market entry brakes, persisted so a redeploy cannot bypass them.</summary>
    public Dictionary<string, DateTime> MarketLossCooldownUntilUtc { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many of ConsecutiveLosses were fee-sized (reporting only; they count toward the streak).</summary>
    public int ConsecutiveSmallLosses { get; set; }

    /// <summary>Realised loss accumulated across the current losing streak, in quote currency.</summary>
    public decimal LossStreakUsd { get; set; }
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
