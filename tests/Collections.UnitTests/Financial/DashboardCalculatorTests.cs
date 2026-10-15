using Collections.Core.Entities;
using Collections.Core.Financial;

namespace Collections.UnitTests.Financial;

public class DashboardCalculatorTests
{
    private static readonly DateOnly AsOf = new(2026, 10, 15);

    private static InvoiceFinancials Inv(decimal amount, decimal paid, int dueOffsetDays)
        => InvoiceCalculator.Calculate(amount, paid, AsOf.AddDays(dueOffsetDays), AsOf);

    [Fact]
    public void Totals_match_hand_calculated_dataset()
    {
        var invoices = new[]
        {
            Inv(10_000m, 0m, -10),      // overdue 10 days, 1–30: 10,000
            Inv(20_000m, 5_000m, -45),  // overdue 45 days, 31–60: 15,000
            Inv(30_000m, 0m, -61),      // overdue 61 days, 61+: 30,000
            Inv(5_000m, 5_000m, -100),  // paid: excluded
            Inv(4_000m, 0m, 0),         // due today: 4,000 (not overdue, in next 7 days)
            Inv(6_000m, 1_000m, 7),     // due in 7 days: 5,000 (in next 7 days, inclusive)
            Inv(8_000m, 0m, 8),         // due in 8 days: 8,000 (not in next 7 days)
            Inv(1_000m, 2_000m, -5),    // invalid: excluded and counted
        };

        var payments = new[]
        {
            new PaymentAmount(new DateOnly(2026, 10, 1), 1_000m),
            new PaymentAmount(new DateOnly(2026, 10, 15), 5_000m),
            new PaymentAmount(new DateOnly(2026, 10, 31), 250m),
            new PaymentAmount(new DateOnly(2026, 9, 30), 99_999m),  // previous month
            new PaymentAmount(new DateOnly(2026, 11, 1), 77_777m),  // next month
        };

        var promises = new[]
        {
            new PromiseSnapshot(PromiseStatus.Open, AsOf.AddDays(-1)),       // missed (system)
            new PromiseSnapshot(PromiseStatus.Open, AsOf),                   // due today: upcoming, not missed
            new PromiseSnapshot(PromiseStatus.Open, AsOf.AddDays(3)),        // upcoming
            new PromiseSnapshot(PromiseStatus.Missed, AsOf.AddDays(-20)),    // user-resolved, not counted
            new PromiseSnapshot(PromiseStatus.Kept, AsOf.AddDays(-20)),
            new PromiseSnapshot(PromiseStatus.Cancelled, AsOf.AddDays(-20)),
        };

        var totals = DashboardCalculator.Compute(invoices, payments, promises, AsOf);

        Assert.Equal(72_000m, totals.TotalOutstanding);       // 10,000 + 15,000 + 30,000 + 4,000 + 5,000 + 8,000
        Assert.Equal(6, totals.OutstandingInvoiceCount);
        Assert.Equal(55_000m, totals.OverdueAmount);
        Assert.Equal(3, totals.OverdueInvoiceCount);
        Assert.Equal(4_000m, totals.DueTodayAmount);
        Assert.Equal(9_000m, totals.DueNext7DaysAmount);
        Assert.Equal(6_250m, totals.CollectionsThisMonth);
        Assert.Equal(1, totals.MissedPromiseCount);
        Assert.Equal(2, totals.UpcomingPromiseCount);
        Assert.Equal(1, totals.InvalidInvoiceCount);

        Assert.Equal(17_000m, totals.Ageing.Single(b => b.Bucket == AgeingBucket.NotDue).Amount);
        Assert.Equal(10_000m, totals.Ageing.Single(b => b.Bucket == AgeingBucket.Days1To30).Amount);
        Assert.Equal(15_000m, totals.Ageing.Single(b => b.Bucket == AgeingBucket.Days31To60).Amount);
        Assert.Equal(30_000m, totals.Ageing.Single(b => b.Bucket == AgeingBucket.Days61Plus).Amount);
        Assert.Equal(totals.TotalOutstanding, totals.Ageing.Sum(b => b.Amount));
    }

    [Fact]
    public void Collections_this_month_uses_business_local_month_of_as_of_date()
    {
        var payments = new[]
        {
            new PaymentAmount(new DateOnly(2026, 2, 28), 100m),
            new PaymentAmount(new DateOnly(2026, 3, 1), 200m),
        };

        var feb = DashboardCalculator.Compute(Array.Empty<InvoiceFinancials>(), payments, Array.Empty<PromiseSnapshot>(), new DateOnly(2026, 2, 28));
        var mar = DashboardCalculator.Compute(Array.Empty<InvoiceFinancials>(), payments, Array.Empty<PromiseSnapshot>(), new DateOnly(2026, 3, 1));

        Assert.Equal(100m, feb.CollectionsThisMonth);
        Assert.Equal(200m, mar.CollectionsThisMonth);
    }

    [Fact]
    public void Business_local_date_differs_from_utc_date_near_midnight()
    {
        // 20:00 UTC on 28 Feb is 01:30 IST on 1 Mar.
        var utc = new DateTimeOffset(2026, 2, 28, 20, 0, 0, TimeSpan.Zero);

        Assert.Equal(new DateOnly(2026, 3, 1), BusinessCalendar.LocalDate(utc, "Asia/Kolkata"));
        Assert.Equal(new DateOnly(2026, 2, 28), DateOnly.FromDateTime(utc.UtcDateTime));
    }

    [Fact]
    public void Empty_data_gives_zero_totals()
    {
        var totals = DashboardCalculator.Compute(Array.Empty<InvoiceFinancials>(), Array.Empty<PaymentAmount>(), Array.Empty<PromiseSnapshot>(), AsOf);

        Assert.Equal(0m, totals.TotalOutstanding);
        Assert.Equal(0, totals.MissedPromiseCount);
        Assert.All(totals.Ageing, b => Assert.Equal(0m, b.Amount));
    }
}
