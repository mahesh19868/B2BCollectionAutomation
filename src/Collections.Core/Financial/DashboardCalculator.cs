using Collections.Core.Entities;

namespace Collections.Core.Financial;

public sealed record PaymentAmount(DateOnly PaymentDate, decimal Amount);

public sealed record PromiseSnapshot(PromiseStatus Status, DateOnly PromisedDate);

public sealed record BucketTotal(AgeingBucket Bucket, decimal Amount, int InvoiceCount);

public sealed record DashboardTotals(
    decimal TotalOutstanding,
    int OutstandingInvoiceCount,
    decimal OverdueAmount,
    int OverdueInvoiceCount,
    decimal DueTodayAmount,
    decimal DueNext7DaysAmount,
    IReadOnlyList<BucketTotal> Ageing,
    decimal CollectionsThisMonth,
    int MissedPromiseCount,
    int UpcomingPromiseCount,
    int InvalidInvoiceCount);

/// <summary>
/// Dashboard definitions (see README):
/// <list type="bullet">
/// <item>Total outstanding: sum of all positive, valid invoice balances.</item>
/// <item>Overdue: balances with DueDate &lt; asOfDate.</item>
/// <item>Due in next 7 days: DueDate from asOfDate to asOfDate + 7 inclusive (includes due today, excludes overdue).</item>
/// <item>Collections this month: payments dated in the business-local calendar month of asOfDate.</item>
/// <item>Missed promises: open promises with PromisedDate &lt; asOfDate (system-calculated, not user-confirmed).</item>
/// </list>
/// </summary>
public static class DashboardCalculator
{
    public static DashboardTotals Compute(
        IEnumerable<InvoiceFinancials> invoices,
        IEnumerable<PaymentAmount> payments,
        IEnumerable<PromiseSnapshot> promises,
        DateOnly asOfDate)
    {
        var all = invoices.ToList();
        var open = all.Where(i => i.HasOutstandingBalance).ToList();
        var overdue = open.Where(i => i.Status == InvoiceStatus.Overdue).ToList();
        var weekEnd = asOfDate.AddDays(7);

        var ageing = new[] { AgeingBucket.NotDue, AgeingBucket.Days1To30, AgeingBucket.Days31To60, AgeingBucket.Days61Plus }
            .Select(b =>
            {
                var inBucket = open.Where(i => i.Bucket == b).ToList();
                return new BucketTotal(b, inBucket.Sum(i => i.Outstanding), inBucket.Count);
            })
            .ToList();

        var monthStart = new DateOnly(asOfDate.Year, asOfDate.Month, 1);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);

        var promiseList = promises.ToList();

        return new DashboardTotals(
            TotalOutstanding: open.Sum(i => i.Outstanding),
            OutstandingInvoiceCount: open.Count,
            OverdueAmount: overdue.Sum(i => i.Outstanding),
            OverdueInvoiceCount: overdue.Count,
            DueTodayAmount: open.Where(i => i.DueDate == asOfDate).Sum(i => i.Outstanding),
            DueNext7DaysAmount: open.Where(i => i.DueDate >= asOfDate && i.DueDate <= weekEnd).Sum(i => i.Outstanding),
            Ageing: ageing,
            CollectionsThisMonth: payments.Where(p => p.PaymentDate >= monthStart && p.PaymentDate <= monthEnd).Sum(p => p.Amount),
            MissedPromiseCount: promiseList.Count(p => PromiseRules.IsOverdue(p.Status, p.PromisedDate, asOfDate)),
            UpcomingPromiseCount: promiseList.Count(p => p.Status == PromiseStatus.Open && p.PromisedDate >= asOfDate),
            InvalidInvoiceCount: all.Count(i => i.Status == InvoiceStatus.InvalidData));
    }
}
