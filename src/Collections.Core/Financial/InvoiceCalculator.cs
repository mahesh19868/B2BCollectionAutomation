namespace Collections.Core.Financial;

public enum InvoiceStatus
{
    NotDue = 1,
    DueToday = 2,
    Overdue = 3,
    Paid = 4,

    /// <summary>Stored data is inconsistent (e.g. allocations exceed the invoice amount). Excluded from totals and reported.</summary>
    InvalidData = 5,
}

public enum AgeingBucket
{
    /// <summary>Not part of ageing: paid or invalid data.</summary>
    None = 0,

    /// <summary>Not due yet or due today (DueDate &gt;= asOfDate).</summary>
    NotDue = 1,
    Days1To30 = 2,
    Days31To60 = 3,
    Days61Plus = 4,
}

/// <summary>Result of the shared invoice calculation for a given as-of date.</summary>
public sealed record InvoiceFinancials(
    decimal InvoiceAmount,
    decimal AllocatedAmount,
    decimal Outstanding,
    InvoiceStatus Status,
    bool IsPartiallyPaid,
    int DaysOverdue,
    AgeingBucket Bucket,
    DateOnly DueDate,
    string? DataError)
{
    /// <summary>True when the invoice has a positive, valid balance and counts towards outstanding totals.</summary>
    public bool HasOutstandingBalance => Status is InvoiceStatus.NotDue or InvoiceStatus.DueToday or InvoiceStatus.Overdue;
}

/// <summary>
/// Single source of truth for invoice balance, status, and ageing.
/// Outstanding = InvoiceAmount - sum of allocated payments. Bad data is reported, never clamped.
/// </summary>
public static class InvoiceCalculator
{
    public static InvoiceFinancials Calculate(decimal invoiceAmount, decimal allocatedAmount, DateOnly dueDate, DateOnly asOfDate)
    {
        var outstanding = invoiceAmount - allocatedAmount;

        string? error = null;
        if (invoiceAmount <= 0)
        {
            error = "Invoice amount must be greater than zero.";
        }
        else if (allocatedAmount < 0)
        {
            error = "Allocated payments are negative.";
        }
        else if (outstanding < 0)
        {
            error = "Allocated payments exceed the invoice amount.";
        }

        if (error is not null)
        {
            return new InvoiceFinancials(invoiceAmount, allocatedAmount, outstanding, InvoiceStatus.InvalidData,
                IsPartiallyPaid: false, DaysOverdue: 0, AgeingBucket.None, dueDate, error);
        }

        if (outstanding == 0)
        {
            return new InvoiceFinancials(invoiceAmount, allocatedAmount, 0m, InvoiceStatus.Paid,
                IsPartiallyPaid: false, DaysOverdue: 0, AgeingBucket.None, dueDate, null);
        }

        var daysOverdue = asOfDate.DayNumber - dueDate.DayNumber;
        var status = daysOverdue switch
        {
            > 0 => InvoiceStatus.Overdue,
            0 => InvoiceStatus.DueToday,
            _ => InvoiceStatus.NotDue,
        };

        return new InvoiceFinancials(
            invoiceAmount,
            allocatedAmount,
            outstanding,
            status,
            IsPartiallyPaid: allocatedAmount > 0,
            DaysOverdue: Math.Max(daysOverdue, 0),
            GetBucket(daysOverdue),
            dueDate,
            null);
    }

    /// <summary>Ageing bucket for a positive balance, from calendar days past the due date.</summary>
    public static AgeingBucket GetBucket(int daysOverdue) => daysOverdue switch
    {
        <= 0 => AgeingBucket.NotDue,
        <= 30 => AgeingBucket.Days1To30,
        <= 60 => AgeingBucket.Days31To60,
        _ => AgeingBucket.Days61Plus,
    };

    public static string Describe(AgeingBucket bucket) => bucket switch
    {
        AgeingBucket.NotDue => "Not due / due today",
        AgeingBucket.Days1To30 => "1–30 days overdue",
        AgeingBucket.Days31To60 => "31–60 days overdue",
        AgeingBucket.Days61Plus => "61+ days overdue",
        _ => "—",
    };

    public static string Describe(InvoiceStatus status) => status switch
    {
        InvoiceStatus.NotDue => "Not due",
        InvoiceStatus.DueToday => "Due today",
        InvoiceStatus.Overdue => "Overdue",
        InvoiceStatus.Paid => "Paid",
        InvoiceStatus.InvalidData => "Data error",
        _ => status.ToString(),
    };
}
