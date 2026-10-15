namespace Collections.Core.Financial;

public static class MoneyRules
{
    /// <summary>Largest amount accepted (fits decimal(18,2)).</summary>
    public const decimal MaxAmount = 9_999_999_999_999.99m;

    public static bool HasAtMostTwoDecimals(decimal value) => decimal.Round(value, 2) == value;

    /// <summary>Returns an error message, or null if the amount is a valid positive money amount.</summary>
    public static string? ValidatePositiveAmount(decimal value)
    {
        if (value <= 0)
        {
            return "Amount must be greater than zero.";
        }

        if (!HasAtMostTwoDecimals(value))
        {
            return "Amount can have at most 2 decimal places.";
        }

        if (value > MaxAmount)
        {
            return "Amount is too large.";
        }

        return null;
    }
}
