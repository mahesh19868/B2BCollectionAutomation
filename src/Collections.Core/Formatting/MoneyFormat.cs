using System.Globalization;

namespace Collections.Core.Formatting;

/// <summary>Culture-independent INR and date formatting used across the UI, emails, and exports.</summary>
public static class MoneyFormat
{
    private static readonly NumberFormatInfo IndianNumberFormat = new()
    {
        CurrencySymbol = "₹",
        CurrencyDecimalDigits = 2,
        CurrencyDecimalSeparator = ".",
        CurrencyGroupSeparator = ",",
        CurrencyGroupSizes = new[] { 3, 2 },
        CurrencyPositivePattern = 0, // ₹n
        CurrencyNegativePattern = 1, // -₹n
        NumberDecimalDigits = 2,
        NumberDecimalSeparator = ".",
        NumberGroupSeparator = ",",
        NumberGroupSizes = new[] { 3, 2 },
        NegativeSign = "-",
    };

    /// <summary>Formats an amount in Indian grouping, e.g. ₹1,25,000.00.</summary>
    public static string Inr(decimal amount) => amount.ToString("C2", IndianNumberFormat);

    /// <summary>Plain invariant amount for inputs and CSV, e.g. 125000.00.</summary>
    public static string Plain(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);
}

public static class DateFormat
{
    public const string DisplayPattern = "dd MMM yyyy";

    public static string Display(DateOnly date) => date.ToString(DisplayPattern, CultureInfo.InvariantCulture);

    public static string Display(DateOnly? date) => date is null ? "—" : Display(date.Value);

    public static string Display(DateTimeOffset utc, string timeZoneId)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        return TimeZoneInfo.ConvertTime(utc, zone).ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture);
    }
}
