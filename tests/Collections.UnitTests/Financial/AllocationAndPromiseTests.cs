using Collections.Core.Entities;
using Collections.Core.Financial;

namespace Collections.UnitTests.Financial;

public class AllocationRulesTests
{
    [Fact]
    public void Valid_allocations_within_payment_and_balances_pass()
    {
        var errors = AllocationRules.Validate(10_000m, new[]
        {
            new AllocationRequest(1, "INV-1", 6_000m, 6_000m),
            new AllocationRequest(2, "INV-2", 9_000m, 3_000m),
        });

        Assert.Empty(errors);
    }

    [Fact]
    public void Partial_allocation_leaves_remainder_unallocated_without_error()
    {
        var errors = AllocationRules.Validate(10_000m, new[] { new AllocationRequest(1, "INV-1", 20_000m, 4_000m) });

        Assert.Empty(errors);
    }

    [Fact]
    public void Allocation_cannot_exceed_invoice_outstanding()
    {
        var errors = AllocationRules.Validate(10_000m, new[] { new AllocationRequest(1, "INV-1", 2_000m, 2_000.01m) });

        Assert.Contains(errors, e => e.Contains("INV-1") && e.Contains("exceeds the outstanding balance"));
    }

    [Fact]
    public void Total_allocations_cannot_exceed_payment_amount()
    {
        var errors = AllocationRules.Validate(5_000m, new[]
        {
            new AllocationRequest(1, "INV-1", 4_000m, 4_000m),
            new AllocationRequest(2, "INV-2", 4_000m, 1_500m),
        });

        Assert.Contains(errors, e => e.Contains("exceeds the payment amount"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10.001)]
    public void Invalid_amounts_are_rejected(double amount)
    {
        var errors = AllocationRules.Validate(100m, new[] { new AllocationRequest(1, "INV-1", 100m, (decimal)amount) });

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Same_invoice_twice_is_rejected()
    {
        var errors = AllocationRules.Validate(100m, new[]
        {
            new AllocationRequest(1, "INV-1", 100m, 10m),
            new AllocationRequest(1, "INV-1", 100m, 10m),
        });

        Assert.Contains(errors, e => e.Contains("more than once"));
    }
}

public class PromiseRulesTests
{
    private static readonly DateOnly AsOf = new(2026, 10, 15);

    [Fact]
    public void Promise_is_not_a_payment_and_does_not_reduce_outstanding()
    {
        // Outstanding is computed only from invoice amount and allocated payments.
        // There is no promise input to the calculation, so a promise cannot change it.
        var before = InvoiceCalculator.Calculate(10_000m, 0m, AsOf.AddDays(-5), AsOf);
        var promise = new PromiseSnapshot(PromiseStatus.Kept, AsOf);
        var after = InvoiceCalculator.Calculate(10_000m, 0m, AsOf.AddDays(-5), AsOf);

        Assert.Equal(PromiseStatus.Kept, promise.Status);
        Assert.Equal(before, after);
        Assert.Equal(10_000m, after.Outstanding);
    }

    [Fact]
    public void Open_promise_is_overdue_only_after_promised_date()
    {
        Assert.False(PromiseRules.IsOverdue(PromiseStatus.Open, AsOf, AsOf));
        Assert.True(PromiseRules.IsOverdue(PromiseStatus.Open, AsOf.AddDays(-1), AsOf));
        Assert.False(PromiseRules.IsOverdue(PromiseStatus.Kept, AsOf.AddDays(-1), AsOf));
        Assert.False(PromiseRules.IsOverdue(PromiseStatus.Missed, AsOf.AddDays(-1), AsOf));
    }

    [Fact]
    public void Passing_the_date_does_not_mark_a_promise_kept_or_missed()
    {
        var state = PromiseRules.GetDisplayState(PromiseStatus.Open, AsOf.AddDays(-30), AsOf);

        Assert.Equal(PromiseDisplayState.PastDueUnresolved, state);
    }

    [Fact]
    public void Resolved_promises_cannot_be_resolved_again()
    {
        Assert.True(PromiseRules.CanResolveTo(PromiseStatus.Open, PromiseStatus.Kept));
        Assert.False(PromiseRules.CanResolveTo(PromiseStatus.Kept, PromiseStatus.Missed));
        Assert.False(PromiseRules.CanResolveTo(PromiseStatus.Open, PromiseStatus.Open));
    }
}

public class WorklistPrioritizerTests
{
    private static readonly DateOnly AsOf = new(2026, 10, 15);

    private static WorklistCandidate C(int id, decimal amount, int dueOffset, bool missedPromise = false)
        => new(id, $"INV-{id}", InvoiceCalculator.Calculate(amount, 0m, AsOf.AddDays(dueOffset), AsOf), missedPromise);

    [Fact]
    public void Sorts_missed_promises_and_severe_overdue_first_then_older_then_larger_then_upcoming()
    {
        var items = new[]
        {
            C(1, 1_000m, 5),                          // upcoming
            C(2, 1_000m, -10),                        // overdue 10
            C(3, 5_000m, -10),                        // overdue 10, larger
            C(4, 1_000m, -20),                        // overdue 20
            C(5, 1_000m, -2, missedPromise: true),    // missed promise
            C(6, 1_000m, -70),                        // 61+
            C(7, 1_000m, 0),                          // due today
        };

        var sorted = WorklistPrioritizer.Sort(items, c => c).Select(c => c.InvoiceId).ToArray();

        Assert.Equal(new[] { 6, 5, 4, 3, 2, 7, 1 }, sorted);
    }

    [Fact]
    public void Ties_are_broken_deterministically()
    {
        var items = new[] { C(9, 1_000m, -10), C(8, 1_000m, -10) };

        var sorted = WorklistPrioritizer.Sort(items, c => c).Select(c => c.InvoiceId).ToArray();

        Assert.Equal(new[] { 8, 9 }, sorted);
    }
}
