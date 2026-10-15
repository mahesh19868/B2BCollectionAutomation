using Collections.Core.Entities;

namespace Collections.Core.Financial;

public enum PromiseDisplayState
{
    Upcoming = 1,
    DueToday = 2,

    /// <summary>System-calculated: still open but the promised date has passed. Not a user-confirmed outcome.</summary>
    PastDueUnresolved = 3,
    Kept = 4,
    Missed = 5,
    Cancelled = 6,
}

/// <summary>
/// A promise is a commitment, not a payment: it never changes invoice outstanding.
/// It is only marked Kept or Missed by an explicit user action.
/// </summary>
public static class PromiseRules
{
    /// <summary>Open promise whose promised date is before asOfDate (shown as a "missed promise" indicator).</summary>
    public static bool IsOverdue(PromiseStatus status, DateOnly promisedDate, DateOnly asOfDate)
        => status == PromiseStatus.Open && promisedDate < asOfDate;

    public static PromiseDisplayState GetDisplayState(PromiseStatus status, DateOnly promisedDate, DateOnly asOfDate) => status switch
    {
        PromiseStatus.Kept => PromiseDisplayState.Kept,
        PromiseStatus.Missed => PromiseDisplayState.Missed,
        PromiseStatus.Cancelled => PromiseDisplayState.Cancelled,
        _ when promisedDate < asOfDate => PromiseDisplayState.PastDueUnresolved,
        _ when promisedDate == asOfDate => PromiseDisplayState.DueToday,
        _ => PromiseDisplayState.Upcoming,
    };

    public static string Describe(PromiseDisplayState state) => state switch
    {
        PromiseDisplayState.Upcoming => "Open – upcoming",
        PromiseDisplayState.DueToday => "Open – due today",
        PromiseDisplayState.PastDueUnresolved => "Open – date passed (not yet resolved)",
        PromiseDisplayState.Kept => "Kept (user confirmed)",
        PromiseDisplayState.Missed => "Missed (user confirmed)",
        PromiseDisplayState.Cancelled => "Cancelled",
        _ => state.ToString(),
    };

    public static bool CanResolveTo(PromiseStatus current, PromiseStatus target)
        => current == PromiseStatus.Open && target is PromiseStatus.Kept or PromiseStatus.Missed or PromiseStatus.Cancelled;
}
