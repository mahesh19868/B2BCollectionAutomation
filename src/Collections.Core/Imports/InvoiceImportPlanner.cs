using Collections.Core.Entities;
using Collections.Core.Financial;
using Collections.Core.Formatting;

namespace Collections.Core.Imports;

public sealed record ExistingCustomer(int Id, string? CustomerCode, string Name, string? Email);

public sealed record ExistingInvoice(
    int Id,
    string InvoiceNumber,
    int CustomerId,
    string CustomerName,
    DateOnly InvoiceDate,
    DateOnly DueDate,
    decimal InvoiceAmount,
    decimal AllocatedAmount);

public sealed record ImportFieldError(string Field, string Message)
{
    public override string ToString() => $"{Field}: {Message}";
}

public sealed class PlannedInvoiceRow
{
    public required InvoiceImportRawRow Raw { get; init; }
    public ImportRowAction Action { get; set; }
    public List<ImportFieldError> Errors { get; } = new();
    public List<string> Notes { get; } = new();

    public string InvoiceNumber { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string? CustomerCode { get; set; }
    public string? CustomerEmail { get; set; }
    public DateOnly InvoiceDate { get; set; }
    public DateOnly DueDate { get; set; }
    public decimal InvoiceAmount { get; set; }

    /// <summary>Set when the row matches an existing customer.</summary>
    public int? ExistingCustomerId { get; set; }

    /// <summary>Set when a new customer must be created; rows sharing a key share the new customer.</summary>
    public string? NewCustomerKey { get; set; }

    public int? ExistingInvoiceId { get; set; }

    public bool HasErrors => Errors.Count > 0;
}

/// <summary>
/// Validates invoice rows and decides, per row, whether it creates, updates, leaves unchanged, or is rejected.
/// Duplicate policy:
/// <list type="bullet">
/// <item>Invoice identity is the invoice number within the business (case-insensitive).</item>
/// <item>An invoice number repeated in the same file rejects every occurrence.</item>
/// <item>An existing invoice with identical dates and amount is skipped as unchanged.</item>
/// <item>An existing invoice with different dates/amount is an update, applied only if the user approves updates.</item>
/// <item>An existing invoice number belonging to a different customer is rejected.</item>
/// <item>Existing customer details (name, email) are never changed by an import.</item>
/// </list>
/// </summary>
public static class InvoiceImportPlanner
{
    public static IReadOnlyList<PlannedInvoiceRow> Plan(
        IReadOnlyList<InvoiceImportRawRow> rows,
        IReadOnlyCollection<ExistingCustomer> existingCustomers,
        IReadOnlyCollection<ExistingInvoice> existingInvoices)
    {
        var planned = rows.Select(ValidateFields).ToList();

        RejectDuplicateInvoiceNumbers(planned);
        RejectConflictingCustomerCodes(planned);

        var customersByCode = existingCustomers
            .Where(c => !string.IsNullOrEmpty(c.CustomerCode))
            .GroupBy(c => c.CustomerCode!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var customersByName = existingCustomers
            .GroupBy(c => c.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var invoicesByNumber = existingInvoices
            .GroupBy(i => i.InvoiceNumber, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var row in planned.Where(r => !r.HasErrors))
        {
            ResolveCustomer(row, customersByCode, customersByName);
            if (!row.HasErrors)
            {
                MatchInvoice(row, invoicesByNumber);
            }
        }

        NoteInconsistentNewCustomerEmails(planned);

        foreach (var row in planned.Where(r => r.HasErrors))
        {
            row.Action = ImportRowAction.Error;
        }

        return planned;
    }

    private static PlannedInvoiceRow ValidateFields(InvoiceImportRawRow raw)
    {
        var row = new PlannedInvoiceRow { Raw = raw };

        if (string.IsNullOrWhiteSpace(raw.CustomerName))
        {
            row.Errors.Add(new(InvoiceCsvReader.CustomerName, "Customer name is required."));
        }
        else if (raw.CustomerName.Length > Customer.NameMaxLength)
        {
            row.Errors.Add(new(InvoiceCsvReader.CustomerName, $"Customer name must be at most {Customer.NameMaxLength} characters."));
        }
        else
        {
            row.CustomerName = raw.CustomerName.Trim();
        }

        if (raw.CustomerCode is not null)
        {
            if (raw.CustomerCode.Length > Customer.CodeMaxLength)
            {
                row.Errors.Add(new(InvoiceCsvReader.CustomerCode, $"Customer code must be at most {Customer.CodeMaxLength} characters."));
            }
            else
            {
                row.CustomerCode = raw.CustomerCode.Trim();
            }
        }

        if (raw.CustomerEmail is not null)
        {
            if (!ImportValueParser.IsValidEmail(raw.CustomerEmail))
            {
                row.Errors.Add(new(InvoiceCsvReader.CustomerEmail, $"'{raw.CustomerEmail}' is not a valid email address."));
            }
            else
            {
                row.CustomerEmail = raw.CustomerEmail.Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(raw.InvoiceNumber))
        {
            row.Errors.Add(new(InvoiceCsvReader.InvoiceNumber, "Invoice number is required."));
        }
        else if (raw.InvoiceNumber.Length > Invoice.NumberMaxLength)
        {
            row.Errors.Add(new(InvoiceCsvReader.InvoiceNumber, $"Invoice number must be at most {Invoice.NumberMaxLength} characters."));
        }
        else if (raw.InvoiceNumber.Any(char.IsControl))
        {
            row.Errors.Add(new(InvoiceCsvReader.InvoiceNumber, "Invoice number contains invalid characters."));
        }
        else
        {
            row.InvoiceNumber = raw.InvoiceNumber.Trim();
        }

        var invoiceDateOk = ParseDate(raw.InvoiceDate, InvoiceCsvReader.InvoiceDate, "Invoice date", row, out var invoiceDate);
        var dueDateOk = ParseDate(raw.DueDate, InvoiceCsvReader.DueDate, "Due date", row, out var dueDate);
        row.InvoiceDate = invoiceDate;
        row.DueDate = dueDate;

        if (invoiceDateOk && dueDateOk && dueDate < invoiceDate)
        {
            row.Errors.Add(new(InvoiceCsvReader.DueDate, "Due date cannot be before the invoice date."));
        }

        if (string.IsNullOrWhiteSpace(raw.InvoiceAmount))
        {
            row.Errors.Add(new(InvoiceCsvReader.InvoiceAmount, "Invoice amount is required."));
        }
        else if (raw.InvoiceAmount.TrimStart().StartsWith('-'))
        {
            row.Errors.Add(new(InvoiceCsvReader.InvoiceAmount, "Invoice amount cannot be negative. Credit notes are not supported."));
        }
        else if (!ImportValueParser.TryParseAmount(raw.InvoiceAmount, out var amount))
        {
            row.Errors.Add(new(InvoiceCsvReader.InvoiceAmount, $"'{raw.InvoiceAmount}' is not a valid amount. Use {ImportValueParser.AmountFormatDescription}."));
        }
        else
        {
            var amountError = MoneyRules.ValidatePositiveAmount(amount);
            if (amountError is not null)
            {
                row.Errors.Add(new(InvoiceCsvReader.InvoiceAmount, amountError));
            }

            row.InvoiceAmount = amount;
        }

        return row;
    }

    private static bool ParseDate(string? text, string field, string label, PlannedInvoiceRow row, out DateOnly date)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            row.Errors.Add(new(field, $"{label} is required."));
            date = default;
            return false;
        }

        if (!ImportValueParser.TryParseDate(text, out date))
        {
            row.Errors.Add(new(field, $"'{text}' is not a valid date. Use {ImportValueParser.DateFormatsDescription}."));
            return false;
        }

        return true;
    }

    private static void RejectDuplicateInvoiceNumbers(List<PlannedInvoiceRow> planned)
    {
        var groups = planned
            .Where(r => r.InvoiceNumber.Length > 0)
            .GroupBy(r => r.InvoiceNumber, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);

        foreach (var group in groups)
        {
            var rowNumbers = string.Join(", ", group.Select(r => r.Raw.RowNumber));
            foreach (var row in group)
            {
                row.Errors.Add(new(InvoiceCsvReader.InvoiceNumber,
                    $"Invoice number '{row.InvoiceNumber}' appears more than once in this file (rows {rowNumbers}). Keep one row per invoice."));
            }
        }
    }

    private static void RejectConflictingCustomerCodes(List<PlannedInvoiceRow> planned)
    {
        var groups = planned
            .Where(r => !string.IsNullOrEmpty(r.CustomerCode) && r.CustomerName.Length > 0)
            .GroupBy(r => r.CustomerCode!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(r => r.CustomerName).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1);

        foreach (var group in groups)
        {
            var rowNumbers = string.Join(", ", group.Select(r => r.Raw.RowNumber));
            foreach (var row in group)
            {
                row.Errors.Add(new(InvoiceCsvReader.CustomerCode,
                    $"Customer code '{row.CustomerCode}' is used with different customer names in this file (rows {rowNumbers})."));
            }
        }
    }

    private static void ResolveCustomer(
        PlannedInvoiceRow row,
        Dictionary<string, ExistingCustomer> byCode,
        Dictionary<string, List<ExistingCustomer>> byName)
    {
        if (!string.IsNullOrEmpty(row.CustomerCode))
        {
            if (byCode.TryGetValue(row.CustomerCode, out var existing))
            {
                UseExisting(row, existing);
                return;
            }

            row.NewCustomerKey = "code:" + row.CustomerCode.ToUpperInvariant();
            if (byName.TryGetValue(row.CustomerName, out var sameName) && sameName.Any(c => string.IsNullOrEmpty(c.CustomerCode)))
            {
                row.Notes.Add($"A customer named '{row.CustomerName}' already exists without a customer code. A new customer with code '{row.CustomerCode}' will be created.");
            }
            else
            {
                row.Notes.Add($"New customer '{row.CustomerName}' ({row.CustomerCode}) will be created.");
            }

            return;
        }

        if (byName.TryGetValue(row.CustomerName, out var matches))
        {
            if (matches.Count > 1)
            {
                row.Errors.Add(new(InvoiceCsvReader.CustomerName,
                    $"{matches.Count} existing customers are named '{row.CustomerName}'. Add a CustomerCode to choose one."));
                return;
            }

            UseExisting(row, matches[0]);
            return;
        }

        row.NewCustomerKey = "name:" + row.CustomerName.ToUpperInvariant();
        row.Notes.Add($"New customer '{row.CustomerName}' will be created.");
    }

    private static void UseExisting(PlannedInvoiceRow row, ExistingCustomer existing)
    {
        row.ExistingCustomerId = existing.Id;

        if (!string.Equals(existing.Name.Trim(), row.CustomerName, StringComparison.Ordinal))
        {
            row.Notes.Add($"Matched existing customer '{existing.Name}'; the existing name is kept.");
        }

        if (row.CustomerEmail is not null && !string.Equals(existing.Email, row.CustomerEmail, StringComparison.OrdinalIgnoreCase))
        {
            row.Notes.Add($"Customer email in file differs from the saved email; the saved email is kept. Edit the customer to change it.");
        }
    }

    private static void MatchInvoice(PlannedInvoiceRow row, Dictionary<string, ExistingInvoice> invoicesByNumber)
    {
        if (!invoicesByNumber.TryGetValue(row.InvoiceNumber, out var existing))
        {
            row.Action = ImportRowAction.Create;
            return;
        }

        row.ExistingInvoiceId = existing.Id;

        if (row.ExistingCustomerId != existing.CustomerId)
        {
            row.Errors.Add(new(InvoiceCsvReader.InvoiceNumber,
                $"Invoice '{existing.InvoiceNumber}' already exists for customer '{existing.CustomerName}'. Invoice numbers must be unique across customers."));
            return;
        }

        var changes = new List<string>();
        if (existing.InvoiceDate != row.InvoiceDate)
        {
            changes.Add($"invoice date {DateFormat.Display(existing.InvoiceDate)} → {DateFormat.Display(row.InvoiceDate)}");
        }

        if (existing.DueDate != row.DueDate)
        {
            changes.Add($"due date {DateFormat.Display(existing.DueDate)} → {DateFormat.Display(row.DueDate)}");
        }

        if (existing.InvoiceAmount != row.InvoiceAmount)
        {
            changes.Add($"amount {MoneyFormat.Inr(existing.InvoiceAmount)} → {MoneyFormat.Inr(row.InvoiceAmount)}");
        }

        if (changes.Count == 0)
        {
            row.Action = ImportRowAction.Unchanged;
            row.Notes.Add("Invoice already exists with the same details; it will be skipped.");
            return;
        }

        if (row.InvoiceAmount < existing.AllocatedAmount)
        {
            row.Errors.Add(new(InvoiceCsvReader.InvoiceAmount,
                $"New amount {MoneyFormat.Inr(row.InvoiceAmount)} is less than payments already allocated ({MoneyFormat.Inr(existing.AllocatedAmount)})."));
            return;
        }

        row.Action = ImportRowAction.Update;
        row.Notes.Add("Existing invoice differs: " + string.Join("; ", changes) + ".");
    }

    private static void NoteInconsistentNewCustomerEmails(List<PlannedInvoiceRow> planned)
    {
        var groups = planned
            .Where(r => !r.HasErrors && r.NewCustomerKey is not null)
            .GroupBy(r => r.NewCustomerKey!);

        foreach (var group in groups)
        {
            var emails = group.Select(r => r.CustomerEmail).Where(e => e is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (emails.Count > 1)
            {
                foreach (var row in group)
                {
                    row.Notes.Add($"Rows for this new customer have different emails; '{emails[0]}' will be used.");
                }
            }
        }
    }

    /// <summary>The email used when creating the new customer identified by <paramref name="newCustomerKey"/>.</summary>
    public static string? EmailForNewCustomer(IEnumerable<PlannedInvoiceRow> rows, string newCustomerKey)
        => rows.Where(r => r.NewCustomerKey == newCustomerKey && !r.HasErrors).Select(r => r.CustomerEmail).FirstOrDefault(e => e is not null);
}
