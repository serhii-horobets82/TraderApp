namespace TraderApp.Risk;

/// <summary>Why the session halted — day-scoped kinds clear at the next UTC midnight.</summary>
public enum HaltKind
{
    None = 0,
    DailyProfit = 1,
    SessionDrawdown = 2,
    PeakDrawdown = 3,
    ConsecutiveLosses = 4,
    MarginCall = 5,
}

public static class HaltKindRules
{
    /// <summary>
    /// Profit / session / peak locks are for "today". Consecutive-loss and
    /// margin-call halts stay until ClearHaltOnBoot.
    /// </summary>
    public static bool ClearsOnUtcDay(HaltKind kind) =>
        kind is HaltKind.DailyProfit or HaltKind.SessionDrawdown or HaltKind.PeakDrawdown;

    public static HaltKind Parse(string? value) =>
        Enum.TryParse<HaltKind>(value, ignoreCase: true, out var k) ? k : HaltKind.None;
}
