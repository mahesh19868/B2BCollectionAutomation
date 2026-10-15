using Collections.Core.Financial;

namespace Collections.UnitTests.Financial;

public class InvoiceCalculatorTests
{
    private static readonly DateOnly AsOf = new(2026, 10, 15);

    [Fact]
    public void Unpaid_invoice_has_full_amount_outstanding()
    {
        var result = InvoiceCalculator.Calculate(10_000m, 0m, AsOf.AddDays(10), AsOf);

        Assert.Equal(10_000m, result.Outstanding);
        Assert.Equal(InvoiceStatus.NotDue, result.Status);
        Assert.False(result.IsPartiallyPaid);
        Assert.True(result.HasOutstandingBalance);
    }

    [Fact]
    public void Fully_paid_invoice_has_zero_outstanding_and_no_bucket()
    {
        var result = InvoiceCalculator.Calculate(10_000m, 10_000m, AsOf.AddDays(-90), AsOf);

        Assert.Equal(0m, result.Outstanding);
        Assert.Equal(InvoiceStatus.Paid, result.Status);
        Assert.Equal(AgeingBucket.None, result.Bucket);
        Assert.False(result.HasOutstandingBalance);
    }

    [Fact]
    public void Partially_paid_invoice_has_correct_balance()
    {
        var result = InvoiceCalculator.Calculate(10_000.50m, 2_500.25m, AsOf.AddDays(-5), AsOf);

        Assert.Equal(7_500.25m, result.Outstanding);
        Assert.True(result.IsPartiallyPaid);
        Assert.Equal(InvoiceStatus.Overdue, result.Status);
    }

    [Fact]
    public void Over_allocated_invoice_is_reported_as_invalid_not_clamped()
    {
        var result = InvoiceCalculator.Calculate(1_000m, 1_200m, AsOf, AsOf);

        Assert.Equal(InvoiceStatus.InvalidData, result.Status);
        Assert.Equal(-200m, result.Outstanding);
        Assert.NotNull(result.DataError);
        Assert.False(result.HasOutstandingBalance);
    }

    [Fact]
    public void Due_today_is_not_overdue()
    {
        var result = InvoiceCalculator.Calculate(500m, 0m, AsOf, AsOf);

        Assert.Equal(InvoiceStatus.DueToday, result.Status);
        Assert.Equal(0, result.DaysOverdue);
        Assert.Equal(AgeingBucket.NotDue, result.Bucket);
    }

    [Theory]
    [InlineData(1, AgeingBucket.Days1To30)]
    [InlineData(30, AgeingBucket.Days1To30)]
    [InlineData(31, AgeingBucket.Days31To60)]
    [InlineData(60, AgeingBucket.Days31To60)]
    [InlineData(61, AgeingBucket.Days61Plus)]
    [InlineData(400, AgeingBucket.Days61Plus)]
    public void Ageing_bucket_boundaries(int daysOverdue, AgeingBucket expected)
    {
        var result = InvoiceCalculator.Calculate(500m, 0m, AsOf.AddDays(-daysOverdue), AsOf);

        Assert.Equal(InvoiceStatus.Overdue, result.Status);
        Assert.Equal(daysOverdue, result.DaysOverdue);
        Assert.Equal(expected, result.Bucket);
    }

    [Fact]
    public void Overdue_calculation_crosses_month_and_year_boundaries_by_calendar_days()
    {
        var result = InvoiceCalculator.Calculate(500m, 0m, new DateOnly(2025, 12, 31), new DateOnly(2026, 3, 1));

        // Jan (31) + Feb 2026 (28) + 1 = 60
        Assert.Equal(60, result.DaysOverdue);
        Assert.Equal(AgeingBucket.Days31To60, result.Bucket);
    }
}
