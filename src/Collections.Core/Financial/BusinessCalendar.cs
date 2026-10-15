namespace Collections.Core.Financial;

/// <summary>Converts a UTC instant to the business-local calendar date used for all due/overdue decisions.</summary>
public static class BusinessCalendar
{
    public static DateOnly LocalDate(DateTimeOffset utcNow, string timeZoneId)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcNow, zone).DateTime);
    }

    public static bool IsValidTimeZone(string timeZoneId)
        => TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out _);
}
