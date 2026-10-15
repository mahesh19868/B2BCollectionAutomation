using System.Data;
using Collections.Core.Entities;
using Collections.Core.Financial;
using Collections.Core.Formatting;
using Collections.Web.Data;
using Collections.Web.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Collections.Web.Services;

public sealed class AllocationInput
{
    public int InvoiceId { get; set; }
    public decimal? Amount { get; set; }
}

public class PaymentInput
{
    public int CustomerId { get; set; }
    public DateOnly? PaymentDate { get; set; }
    public decimal? Amount { get; set; }
    public string? Reference { get; set; }
    public string? Notes { get; set; }
    public List<AllocationInput> Allocations { get; set; } = new();
}

public sealed record OpenInvoiceOption(int InvoiceId, string InvoiceNumber, DateOnly DueDate, decimal Outstanding, InvoiceStatus Status, int DaysOverdue);

public sealed record PaymentDetail(
    int Id, int CustomerId, string CustomerName, DateOnly PaymentDate, decimal Amount, string? Reference, string? Notes,
    DateTimeOffset CreatedAtUtc, IReadOnlyList<(int InvoiceId, string InvoiceNumber, decimal Amount)> Allocations)
{
    public decimal Unallocated => Amount - Allocations.Sum(a => a.Amount);
}

/// <summary>Manual payment entry with allocation to one or more invoices of the same customer.</summary>
public sealed class PaymentService
{
    public const int PageSize = 50;

    private readonly AppDbContext _db;
    private readonly BusinessService _business;
    private readonly InvoiceQueryService _invoices;
    private readonly ICurrentBusinessContext _context;
    private readonly TimeProvider _time;

    public PaymentService(AppDbContext db, BusinessService business, InvoiceQueryService invoices,
        ICurrentBusinessContext context, TimeProvider time)
    {
        _db = db;
        _business = business;
        _invoices = invoices;
        _context = context;
        _time = time;
    }

    public async Task<PagedResult<PaymentListItem>> ListAsync(string? search, int page, CancellationToken ct)
    {
        var query = _db.Payments.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(p => p.Customer!.Name.Contains(term) || (p.Reference != null && p.Reference.Contains(term)));
        }

        var total = await query.CountAsync(ct);
        page = Math.Clamp(page, 1, Math.Max(1, (int)Math.Ceiling(total / (double)PageSize)));
        var items = await query
            .OrderByDescending(p => p.PaymentDate).ThenByDescending(p => p.Id)
            .Skip((page - 1) * PageSize).Take(PageSize)
            .Select(p => new PaymentListItem(p.Id, p.CustomerId, p.Customer!.Name, p.PaymentDate, p.Amount,
                p.Allocations.Sum(a => (decimal?)a.AllocatedAmount) ?? 0m, p.Reference))
            .ToListAsync(ct);
        return new PagedResult<PaymentListItem>(items, page, PageSize, total);
    }

    public async Task<IReadOnlyList<OpenInvoiceOption>> GetOpenInvoicesAsync(int customerId, CancellationToken ct)
    {
        var today = await _business.GetTodayAsync(ct);
        return (await _invoices.GetAsync(q => q.Where(i => i.CustomerId == customerId), nonZeroBalanceOnly: true, today, ct))
            .Where(i => i.Financials.HasOutstandingBalance)
            .OrderBy(i => i.DueDate).ThenBy(i => i.InvoiceNumber).ThenBy(i => i.Id)
            .Select(i => new OpenInvoiceOption(i.Id, i.InvoiceNumber, i.DueDate, i.Financials.Outstanding, i.Financials.Status, i.Financials.DaysOverdue))
            .ToList();
    }

    public async Task<PaymentDetail?> GetDetailAsync(int id, CancellationToken ct)
    {
        var payment = await _db.Payments.AsNoTracking()
            .Where(p => p.Id == id)
            .Select(p => new
            {
                p.Id, p.CustomerId, CustomerName = p.Customer!.Name, p.PaymentDate, p.Amount, p.Reference, p.Notes, p.CreatedAtUtc,
                Allocations = p.Allocations.OrderBy(a => a.Invoice!.DueDate).Select(a => new { a.InvoiceId, a.Invoice!.InvoiceNumber, a.AllocatedAmount }).ToList(),
            })
            .SingleOrDefaultAsync(ct);

        return payment is null
            ? null
            : new PaymentDetail(payment.Id, payment.CustomerId, payment.CustomerName, payment.PaymentDate, payment.Amount, payment.Reference,
                payment.Notes, payment.CreatedAtUtc, payment.Allocations.Select(a => (a.InvoiceId, a.InvoiceNumber, a.AllocatedAmount)).ToList());
    }

    public async Task<ServiceResult> RecordAsync(PaymentInput input, CancellationToken ct)
    {
        var today = await _business.GetTodayAsync(ct);
        var errors = new List<ServiceError>();

        if (input.PaymentDate is null)
        {
            errors.Add(new(nameof(input.PaymentDate), "Payment date is required."));
        }
        else if (input.PaymentDate > today)
        {
            errors.Add(new(nameof(input.PaymentDate), "Payment date cannot be in the future."));
        }
        else if (input.PaymentDate.Value.Year < 2000)
        {
            errors.Add(new(nameof(input.PaymentDate), "Payment date is not valid."));
        }

        if (input.Amount is null)
        {
            errors.Add(new(nameof(input.Amount), "Payment amount is required."));
        }
        else if (MoneyRules.ValidatePositiveAmount(input.Amount.Value) is { } amountError)
        {
            errors.Add(new(nameof(input.Amount), amountError));
        }

        var reference = string.IsNullOrWhiteSpace(input.Reference) ? null : input.Reference.Trim();
        if (reference?.Length > Payment.ReferenceMaxLength)
        {
            errors.Add(new(nameof(input.Reference), $"Reference must be at most {Payment.ReferenceMaxLength} characters."));
        }

        var notes = string.IsNullOrWhiteSpace(input.Notes) ? null : input.Notes.Trim();
        if (notes?.Length > Payment.NotesMaxLength)
        {
            errors.Add(new(nameof(input.Notes), $"Notes must be at most {Payment.NotesMaxLength} characters."));
        }

        if (!await _db.Customers.AnyAsync(c => c.Id == input.CustomerId, ct))
        {
            return ServiceResult.Missing();
        }

        if (reference is not null && await _db.Payments.AnyAsync(p => p.CustomerId == input.CustomerId && p.Reference == reference, ct))
        {
            errors.Add(new(nameof(input.Reference), "A payment with this reference is already recorded for this customer."));
        }

        if (errors.Count > 0)
        {
            return ServiceResult.Fail(errors);
        }

        // Blank or zero rows mean "not allocated to this invoice"; negative amounts are kept so the rules reject them.
        var requested = input.Allocations.Where(a => a.Amount.HasValue && a.Amount.Value != 0).ToList();

        // Serializable so two concurrent payments cannot both allocate the same outstanding balance.
        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);

        var invoiceIds = requested.Select(a => a.InvoiceId).Distinct().ToList();
        var invoices = (await _invoices.GetAsync(q => q.Where(i => invoiceIds.Contains(i.Id) && i.CustomerId == input.CustomerId),
                nonZeroBalanceOnly: false, today, ct))
            .ToDictionary(i => i.Id);

        if (invoiceIds.Any(id => !invoices.ContainsKey(id)))
        {
            return ServiceResult.Fail(string.Empty, "One or more selected invoices do not belong to this customer.");
        }

        var allocationRequests = requested
            .Select(a => new AllocationRequest(a.InvoiceId, invoices[a.InvoiceId].InvoiceNumber,
                invoices[a.InvoiceId].Financials.HasOutstandingBalance ? invoices[a.InvoiceId].Financials.Outstanding : 0m, a.Amount!.Value))
            .ToList();

        var ruleErrors = AllocationRules.Validate(input.Amount!.Value, allocationRequests);
        if (ruleErrors.Count > 0)
        {
            return ServiceResult.Fail(ruleErrors.Select(e => new ServiceError(string.Empty, e)));
        }

        var now = _time.GetUtcNow();
        var payment = new Payment
        {
            CustomerId = input.CustomerId,
            PaymentDate = input.PaymentDate!.Value,
            Amount = input.Amount.Value,
            Reference = reference,
            Notes = notes,
            CreatedAtUtc = now,
            CreatedByUserId = _context.UserId,
            Allocations = allocationRequests.Select(a => new PaymentAllocation { InvoiceId = a.InvoiceId, AllocatedAmount = a.Amount }).ToList(),
        };
        _db.Payments.Add(payment);

        var allocatedText = allocationRequests.Count == 0
            ? "not allocated to any invoice"
            : "allocated to " + string.Join(", ", allocationRequests.Select(a => $"{a.InvoiceNumber} ({MoneyFormat.Inr(a.Amount)})"));
        _db.AddSystemNote(input.CustomerId, null,
            $"Payment of {MoneyFormat.Inr(payment.Amount)} dated {DateFormat.Display(payment.PaymentDate)} recorded{(reference is null ? "" : $" (ref {reference})")}; {allocatedText}.",
            _context.UserId, now);

        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return ServiceResult.Ok(payment.Id);
    }

    /// <summary>Removes a payment entered in error, with its allocations. The removal is recorded in the customer history.</summary>
    public async Task<ServiceResult> DeleteAsync(int id, CancellationToken ct)
    {
        var payment = await _db.Payments.Include(p => p.Allocations).ThenInclude(a => a.Invoice).SingleOrDefaultAsync(p => p.Id == id, ct);
        if (payment is null)
        {
            return ServiceResult.Missing();
        }

        var allocations = payment.Allocations.Count == 0
            ? "no allocations"
            : "allocations removed from " + string.Join(", ", payment.Allocations.Select(a => $"{a.Invoice!.InvoiceNumber} ({MoneyFormat.Inr(a.AllocatedAmount)})"));
        _db.AddSystemNote(payment.CustomerId, null,
            $"Payment of {MoneyFormat.Inr(payment.Amount)} dated {DateFormat.Display(payment.PaymentDate)}{(payment.Reference is null ? "" : $" (ref {payment.Reference})")} deleted; {allocations}.",
            _context.UserId, _time.GetUtcNow());

        _db.Payments.Remove(payment);
        await _db.SaveChangesAsync(ct);
        return ServiceResult.Ok(payment.CustomerId);
    }
}
