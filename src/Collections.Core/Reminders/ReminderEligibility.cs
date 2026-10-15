using Collections.Core.Financial;
using Collections.Core.Formatting;
using Collections.Core.Imports;

namespace Collections.Core.Reminders;

public sealed record ReminderEligibilityInput(
    string? CustomerEmail,
    bool CustomerIsActive,
    bool CustomerRemindersPaused,
    bool InvoiceIsDisputed,
    bool InvoiceRemindersPaused,
    InvoiceFinancials Financials,
    DateTimeOffset? LastSentAtUtc,
    bool HasSendInProgress,
    int CooldownDays,
    DateTimeOffset NowUtc);

/// <summary>
/// Decides whether an invoice reminder may be sent. Returns every reason it may not;
/// an empty list means eligible.
/// </summary>
public static class ReminderEligibility
{
    public static IReadOnlyList<string> Evaluate(ReminderEligibilityInput input)
    {
        var reasons = new List<string>();

        if (string.IsNullOrWhiteSpace(input.CustomerEmail))
        {
            reasons.Add("The customer has no email address.");
        }
        else if (!ImportValueParser.IsValidEmail(input.CustomerEmail))
        {
            reasons.Add("The customer's email address is not valid.");
        }

        if (!input.CustomerIsActive)
        {
            reasons.Add("The customer is inactive.");
        }

        if (input.CustomerRemindersPaused)
        {
            reasons.Add("Reminders are paused for this customer.");
        }

        if (input.InvoiceRemindersPaused)
        {
            reasons.Add("Reminders are paused for this invoice.");
        }

        if (input.InvoiceIsDisputed)
        {
            reasons.Add("The invoice is marked as disputed.");
        }

        switch (input.Financials.Status)
        {
            case InvoiceStatus.Paid:
                reasons.Add("The invoice is fully paid.");
                break;
            case InvoiceStatus.InvalidData:
                reasons.Add($"The invoice has a data error: {input.Financials.DataError}");
                break;
        }

        if (input.HasSendInProgress)
        {
            reasons.Add("Another reminder for this invoice is currently being sent.");
        }

        if (input.LastSentAtUtc is { } lastSent && input.CooldownDays > 0)
        {
            var cooldownEnds = lastSent.AddDays(input.CooldownDays);
            if (input.NowUtc < cooldownEnds)
            {
                reasons.Add($"A reminder was already sent within the last {input.CooldownDays} day(s). The next reminder can be sent after {cooldownEnds.UtcDateTime:dd MMM yyyy HH:mm} UTC.");
            }
        }

        return reasons;
    }

    public static Dictionary<string, string> BuildTemplateValues(
        string customerName,
        string invoiceNumber,
        DateOnly invoiceDate,
        DateOnly dueDate,
        decimal invoiceAmount,
        decimal outstanding,
        string businessName,
        string businessContact)
        => new(StringComparer.Ordinal)
        {
            ["CustomerName"] = customerName,
            ["InvoiceNumber"] = invoiceNumber,
            ["InvoiceDate"] = DateFormat.Display(invoiceDate),
            ["DueDate"] = DateFormat.Display(dueDate),
            ["InvoiceAmount"] = MoneyFormat.Inr(invoiceAmount),
            ["OutstandingAmount"] = MoneyFormat.Inr(outstanding),
            ["BusinessName"] = businessName,
            ["BusinessContact"] = businessContact,
        };
}
