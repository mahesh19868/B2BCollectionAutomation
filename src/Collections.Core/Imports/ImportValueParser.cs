using System.Globalization;
using System.Net.Mail;

namespace Collections.Core.Imports;

/// <summary>Culture-independent parsing of import values. Accepted formats are documented in the template.</summary>
public static class ImportValueParser
{
    /// <summary>Accepted date formats. Slashed/dashed numeric dates are day-first (Indian convention).</summary>
    public static readonly string[] DateFormats =
    {
        "yyyy-MM-dd",
        "dd-MM-yyyy",
        "d-M-yyyy",
        "dd/MM/yyyy",
        "d/M/yyyy",
        "dd-MMM-yyyy",
        "d-MMM-yyyy",
        "dd MMM yyyy",
        "d MMM yyyy",
    };

    public const string DateFormatsDescription = "YYYY-MM-DD, DD-MM-YYYY, DD/MM/YYYY or DD-Mon-YYYY (e.g. 2026-04-15, 15/04/2026, 15-Apr-2026)";

    public const string AmountFormatDescription = "digits with an optional decimal point and up to 2 decimals; commas allowed as thousands separators; no currency symbol (e.g. 125000.00 or 1,25,000.00)";

    public static bool TryParseDate(string? text, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return DateOnly.TryParseExact(text.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
            && date.Year is >= 2000 and <= 2100;
    }

    public static bool TryParseAmount(string? text, out decimal amount)
    {
        amount = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();

        // Only digits, commas and one decimal point. Commas must not follow the decimal point.
        var dot = trimmed.IndexOf('.');
        foreach (var ch in trimmed)
        {
            if (!(char.IsAsciiDigit(ch) || ch == ',' || ch == '.'))
            {
                return false;
            }
        }

        if (trimmed.Count(ch => ch == '.') > 1 || trimmed.StartsWith(',') || (dot >= 0 && trimmed.IndexOf(',', dot) >= 0))
        {
            return false;
        }

        return decimal.TryParse(trimmed.Replace(",", string.Empty), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount);
    }

    public static bool IsValidEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > 254 || email.Any(char.IsWhiteSpace))
        {
            return false;
        }

        if (!MailAddress.TryCreate(email, out var parsed) || parsed.Address != email || !string.IsNullOrEmpty(parsed.DisplayName))
        {
            return false;
        }

        var domain = parsed.Host;
        return domain.Contains('.') && !domain.StartsWith('.') && !domain.EndsWith('.');
    }
}
