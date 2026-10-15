namespace Collections.Core.Financial;

public sealed record WorklistCandidate(
    int InvoiceId,
    string InvoiceNumber,
    InvoiceFinancials Financials,
    bool HasMissedPromise);

/// <summary>
/// Deterministic, explainable worklist ordering (no opaque score):
/// <list type="number">
/// <item>Priority group 1: an open promise whose date has passed, or more than 60 days overdue.</item>
/// <item>Priority group 2: other overdue invoices.</item>
/// <item>Priority group 3: due today and upcoming invoices.</item>
/// </list>
/// Within groups 1–2: most days overdue first, then larger balance. Within group 3: earliest due date, then larger balance.
/// Ties are broken by invoice number and id.
/// </summary>
public static class WorklistPrioritizer
{
    public const string Explanation =
        "Sorted by: (1) missed promises and invoices more than 60 days overdue, (2) other overdue invoices, " +
        "(3) due today and upcoming. Within overdue groups: most days overdue first, then larger balance. " +
        "Upcoming: earliest due date first, then larger balance.";

    public static int PriorityGroup(WorklistCandidate c)
    {
        if (c.HasMissedPromise || c.Financials.Bucket == AgeingBucket.Days61Plus)
        {
            return 1;
        }

        return c.Financials.Status == InvoiceStatus.Overdue ? 2 : 3;
    }

    public static IReadOnlyList<T> Sort<T>(IEnumerable<T> items, Func<T, WorklistCandidate> candidate)
    {
        return items
            .Select(item => (item, c: candidate(item)))
            .OrderBy(x => PriorityGroup(x.c))
            .ThenByDescending(x => x.c.Financials.DaysOverdue)
            .ThenBy(x => x.c.Financials.DueDate)
            .ThenByDescending(x => x.c.Financials.Outstanding)
            .ThenBy(x => x.c.InvoiceNumber, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.c.InvoiceId)
            .Select(x => x.item)
            .ToList();
    }
}
