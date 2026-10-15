using Collections.Core.Entities;
using Collections.Core.Financial;
using Collections.Core.Formatting;
using Collections.Web.Data;
using Collections.Web.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Collections.Web.Services;

public class InvoiceInput
{
    public int CustomerId { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public DateOnly? InvoiceDate { get; set; }
    public DateOnly? DueDate { get; set; }
    public decimal? InvoiceAmount { get; set; }
    public string? ExternalReference { get; set; }
    public bool IsDisputed { get; set; }
    public bool RemindersPaused { get; set; }
}

public enum InvoiceListStatus
{
    Outstanding = 0,
    Overdue = 1,
    DueToday = 2,
    NotDue = 3,
    Paid = 4,
    DataError = 5,
    All = 6,
}

public class InvoiceListFilter
{
    public InvoiceListStatus Status { get; set; } = InvoiceListStatus.Outstanding;
    public AgeingBucket? Bucket { get; set; }
    public string? Search { get; set; }
    public int? CustomerId { get; set; }
    public int Page { get; set; } = 1;
}

public sealed record AllocationItem(int PaymentId, DateOnly PaymentDate, string? Reference, decimal AllocatedAmount);

public sealed record InvoiceDetail(
    InvoiceSummary Summary,
    string? ExternalReference,
    DateOnly AsOfDate,
    IReadOnlyList<AllocationItem> Allocations,
    IReadOnlyList<PromiseItem> Promises,
    IReadOnlyList<ActivityItem> Activities,
    IReadOnlyList<ReminderItem> Reminders);

public sealed class InvoiceService
{
    public const int PageSize = 50;

    private readonly AppDbContext _db;
    private readonly BusinessService _business;
    private readonly InvoiceQueryService _invoices;
    private readonly ActivityQueries _queries;
    private readonly ICurrentBusinessContext _context;
    private readonly TimeProvider _time;

    public InvoiceService(AppDbContext db, BusinessService business, InvoiceQueryService invoices, ActivityQueries queries,
        ICurrentBusinessContext context, TimeProvider time)
    {
        _db = db;
        _business = business;
        _invoices = invoices;
        _queries = queries;
        _context = context;
        _time = time;
    }

    /// <summary>Server-side filtering and paging. Balances come from the shared calculator.</summary>
    public async Task<(PagedResult<InvoiceSummary> Page, DateOnly AsOf)> ListAsync(InvoiceListFilter filter, CancellationToken ct)
    {
        var today = await _business.GetTodayAsync(ct);
        var search = filter.Search?.Trim();

        var rows = await _invoices.GetAsync(q =>
        {
            if (filter.CustomerId is { } customerId)
            {
                q = q.Where(i => i.CustomerId == customerId);
            }

            if (!string.IsNullOrEmpty(search))
            {
                q = q.Where(i => i.InvoiceNumber.Contains(search) || i.Customer!.Name.Contains(search) ||
                                 (i.Customer.CustomerCode != null && i.Customer.CustomerCode.Contains(search)));
            }

            return q;
        }, nonZeroBalanceOnly: filter.Status is not (InvoiceListStatus.All or InvoiceListStatus.Paid), today, ct);

        IEnumerable<InvoiceSummary> filtered = filter.Status switch
        {
            InvoiceListStatus.Outstanding => rows.Where(r => r.Financials.HasOutstandingBalance),
            InvoiceListStatus.Overdue => rows.Where(r => r.Financials.Status == InvoiceStatus.Overdue),
            InvoiceListStatus.DueToday => rows.Where(r => r.Financials.Status == InvoiceStatus.DueToday),
            InvoiceListStatus.NotDue => rows.Where(r => r.Financials.Status == InvoiceStatus.NotDue),
            InvoiceListStatus.Paid => rows.Where(r => r.Financials.Status == InvoiceStatus.Paid),
            InvoiceListStatus.DataError => rows.Where(r => r.Financials.Status == InvoiceStatus.InvalidData),
            _ => rows,
        };

        if (filter.Bucket is { } bucket)
        {
            filtered = filtered.Where(r => r.Financials.Bucket == bucket);
        }

        var ordered = filtered
            .OrderBy(r => r.DueDate).ThenBy(r => r.InvoiceNumber, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Id)
            .ToList();

        var totalPages = Math.Max(1, (int)Math.Ceiling(ordered.Count / (double)PageSize));
        var page = Math.Clamp(filter.Page, 1, totalPages);
        var items = ordered.Skip((page - 1) * PageSize).Take(PageSize).ToList();
        return (new PagedResult<InvoiceSummary>(items, page, PageSize, ordered.Count), today);
    }

    public async Task<InvoiceDetail?> GetDetailAsync(int id, CancellationToken ct)
    {
        var today = await _business.GetTodayAsync(ct);
        var summary = await _invoices.GetOneAsync(id, today, ct);
        if (summary is null)
        {
            return null;
        }

        var externalReference = await _db.Invoices.Where(i => i.Id == id).Select(i => i.ExternalReference).SingleAsync(ct);
        var allocations = await _db.PaymentAllocations.AsNoTracking()
            .Where(a => a.InvoiceId == id)
            .OrderBy(a => a.Payment!.PaymentDate).ThenBy(a => a.Id)
            .Select(a => new AllocationItem(a.PaymentId, a.Payment!.PaymentDate, a.Payment.Reference, a.AllocatedAmount))
            .ToListAsync(ct);

        return new InvoiceDetail(
            summary,
            externalReference,
            today,
            allocations,
            await _queries.PromisesAsync(p => p.InvoiceId == id, today, 100, ct),
            await _queries.ActivitiesAsync(a => a.InvoiceId == id, 100, ct),
            await _queries.RemindersAsync(r => r.InvoiceId == id, 50, ct));
    }

    public async Task<InvoiceInput?> GetForEditAsync(int id, CancellationToken ct)
        => await _db.Invoices.AsNoTracking().Where(i => i.Id == id)
            .Select(i => new InvoiceInput
            {
                CustomerId = i.CustomerId,
                InvoiceNumber = i.InvoiceNumber,
                InvoiceDate = i.InvoiceDate,
                DueDate = i.DueDate,
                InvoiceAmount = i.InvoiceAmount,
                ExternalReference = i.ExternalReference,
                IsDisputed = i.IsDisputed,
                RemindersPaused = i.RemindersPaused,
            })
            .SingleOrDefaultAsync(ct);

    public async Task<ServiceResult> CreateAsync(InvoiceInput input, CancellationToken ct)
    {
        var errors = ValidateCommon(input);
        var number = input.InvoiceNumber?.Trim() ?? string.Empty;
        if (number.Length == 0)
        {
            errors.Add(new(nameof(input.InvoiceNumber), "Invoice number is required."));
        }
        else if (number.Length > Invoice.NumberMaxLength)
        {
            errors.Add(new(nameof(input.InvoiceNumber), $"Invoice number must be at most {Invoice.NumberMaxLength} characters."));
        }
        else if (await _db.Invoices.AnyAsync(i => i.InvoiceNumber == number, ct))
        {
            errors.Add(new(nameof(input.InvoiceNumber), "An invoice with this number already exists."));
        }

        // Tenant check: the customer must be visible through the business-scoped query filter.
        if (!await _db.Customers.AnyAsync(c => c.Id == input.CustomerId, ct))
        {
            errors.Add(new(nameof(input.CustomerId), "Select a customer."));
        }

        if (errors.Count > 0)
        {
            return ServiceResult.Fail(errors);
        }

        var invoice = new Invoice
        {
            CustomerId = input.CustomerId,
            InvoiceNumber = number,
            InvoiceDate = input.InvoiceDate!.Value,
            DueDate = input.DueDate!.Value,
            InvoiceAmount = input.InvoiceAmount!.Value,
            ExternalReference = Clean(input.ExternalReference),
            IsDisputed = input.IsDisputed,
            RemindersPaused = input.RemindersPaused,
        };
        _db.Invoices.Add(invoice);
        await _db.SaveChangesAsync(ct);
        return ServiceResult.Ok(invoice.Id);
    }

    /// <summary>Corrects dates/amount/flags. Invoice number and customer are fixed. Changes are logged in the history.</summary>
    public async Task<ServiceResult> UpdateAsync(int id, InvoiceInput input, CancellationToken ct)
    {
        var invoice = await _db.Invoices.SingleOrDefaultAsync(i => i.Id == id, ct);
        if (invoice is null)
        {
            return ServiceResult.Missing();
        }

        var errors = ValidateCommon(input);
        if (errors.Count == 0)
        {
            var allocated = await _db.PaymentAllocations.Where(a => a.InvoiceId == id).SumAsync(a => (decimal?)a.AllocatedAmount, ct) ?? 0m;
            if (input.InvoiceAmount < allocated)
            {
                errors.Add(new(nameof(input.InvoiceAmount), $"Amount cannot be less than payments already allocated ({MoneyFormat.Inr(allocated)})."));
            }
        }

        if (errors.Count > 0)
        {
            return ServiceResult.Fail(errors);
        }

        var changes = new List<string>();
        if (invoice.InvoiceDate != input.InvoiceDate)
        {
            changes.Add($"invoice date {DateFormat.Display(invoice.InvoiceDate)} → {DateFormat.Display(input.InvoiceDate!.Value)}");
        }

        if (invoice.DueDate != input.DueDate)
        {
            changes.Add($"due date {DateFormat.Display(invoice.DueDate)} → {DateFormat.Display(input.DueDate!.Value)}");
        }

        if (invoice.InvoiceAmount != input.InvoiceAmount)
        {
            changes.Add($"amount {MoneyFormat.Inr(invoice.InvoiceAmount)} → {MoneyFormat.Inr(input.InvoiceAmount!.Value)}");
        }

        if (invoice.IsDisputed != input.IsDisputed)
        {
            changes.Add(input.IsDisputed ? "marked disputed" : "dispute cleared");
        }

        if (invoice.RemindersPaused != input.RemindersPaused)
        {
            changes.Add(input.RemindersPaused ? "reminders paused" : "reminders resumed");
        }

        invoice.InvoiceDate = input.InvoiceDate!.Value;
        invoice.DueDate = input.DueDate!.Value;
        invoice.InvoiceAmount = input.InvoiceAmount!.Value;
        invoice.ExternalReference = Clean(input.ExternalReference);
        invoice.IsDisputed = input.IsDisputed;
        invoice.RemindersPaused = input.RemindersPaused;

        if (changes.Count > 0)
        {
            AddSystemNote(invoice.CustomerId, invoice.Id, $"Invoice {invoice.InvoiceNumber} edited: {string.Join("; ", changes)}.");
        }

        await _db.SaveChangesAsync(ct);
        return ServiceResult.Ok(id);
    }

    public async Task<ServiceResult> SetFlagsAsync(int id, bool? disputed, bool? remindersPaused, CancellationToken ct)
    {
        var invoice = await _db.Invoices.SingleOrDefaultAsync(i => i.Id == id, ct);
        if (invoice is null)
        {
            return ServiceResult.Missing();
        }

        if (disputed is { } d && d != invoice.IsDisputed)
        {
            invoice.IsDisputed = d;
            AddSystemNote(invoice.CustomerId, id, $"Invoice {invoice.InvoiceNumber} {(d ? "marked disputed" : "dispute cleared")}.");
        }

        if (remindersPaused is { } p && p != invoice.RemindersPaused)
        {
            invoice.RemindersPaused = p;
            AddSystemNote(invoice.CustomerId, id, $"Reminders {(p ? "paused" : "resumed")} for invoice {invoice.InvoiceNumber}.");
        }

        await _db.SaveChangesAsync(ct);
        return ServiceResult.Ok(id);
    }

    private void AddSystemNote(int customerId, int? invoiceId, string text)
        => _db.AddSystemNote(customerId, invoiceId, text, _context.UserId, _time.GetUtcNow());

    private static List<ServiceError> ValidateCommon(InvoiceInput input)
    {
        var errors = new List<ServiceError>();
        if (input.InvoiceDate is null)
        {
            errors.Add(new(nameof(input.InvoiceDate), "Invoice date is required."));
        }

        if (input.DueDate is null)
        {
            errors.Add(new(nameof(input.DueDate), "Due date is required."));
        }
        else if (input.InvoiceDate is not null && input.DueDate < input.InvoiceDate)
        {
            errors.Add(new(nameof(input.DueDate), "Due date cannot be before the invoice date."));
        }

        if (input.InvoiceAmount is null)
        {
            errors.Add(new(nameof(input.InvoiceAmount), "Invoice amount is required."));
        }
        else if (MoneyRules.ValidatePositiveAmount(input.InvoiceAmount.Value) is { } amountError)
        {
            errors.Add(new(nameof(input.InvoiceAmount), amountError));
        }

        if (input.ExternalReference?.Trim().Length > Invoice.ExternalReferenceMaxLength)
        {
            errors.Add(new(nameof(input.ExternalReference), $"Reference must be at most {Invoice.ExternalReferenceMaxLength} characters."));
        }

        return errors;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
