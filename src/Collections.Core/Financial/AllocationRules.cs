using Collections.Core.Formatting;

namespace Collections.Core.Financial;

/// <param name="InvoiceOutstanding">Current outstanding of the invoice before this allocation.</param>
public sealed record AllocationRequest(int InvoiceId, string InvoiceNumber, decimal InvoiceOutstanding, decimal Amount);

/// <summary>
/// Rules for allocating a payment to invoices:
/// each allocation is positive and cannot exceed the invoice's outstanding balance;
/// the total allocated cannot exceed the payment amount. Any remainder stays visible as unallocated.
/// </summary>
public static class AllocationRules
{
    public static IReadOnlyList<string> Validate(decimal paymentAmount, IReadOnlyCollection<AllocationRequest> allocations)
    {
        var errors = new List<string>();

        var paymentError = MoneyRules.ValidatePositiveAmount(paymentAmount);
        if (paymentError is not null)
        {
            errors.Add($"Payment amount: {paymentError}");
        }

        foreach (var duplicate in allocations.GroupBy(a => a.InvoiceId).Where(g => g.Count() > 1))
        {
            errors.Add($"Invoice {duplicate.First().InvoiceNumber} is allocated more than once.");
        }

        foreach (var allocation in allocations)
        {
            var amountError = MoneyRules.ValidatePositiveAmount(allocation.Amount);
            if (amountError is not null)
            {
                errors.Add($"Invoice {allocation.InvoiceNumber}: {amountError}");
                continue;
            }

            if (allocation.Amount > allocation.InvoiceOutstanding)
            {
                errors.Add($"Invoice {allocation.InvoiceNumber}: allocation of {MoneyFormat.Inr(allocation.Amount)} exceeds the outstanding balance of {MoneyFormat.Inr(allocation.InvoiceOutstanding)}.");
            }
        }

        var total = allocations.Sum(a => a.Amount);
        if (paymentError is null && total > paymentAmount)
        {
            errors.Add($"Total allocated ({MoneyFormat.Inr(total)}) exceeds the payment amount ({MoneyFormat.Inr(paymentAmount)}).");
        }

        return errors;
    }
}
