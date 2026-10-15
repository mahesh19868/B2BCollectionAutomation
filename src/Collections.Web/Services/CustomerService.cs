using Collections.Core.Entities;
using Collections.Core.Financial;
using Collections.Core.Imports;
using Collections.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Collections.Web.Services;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
}

public class CustomerInput
{
    public string Name { get; set; } = string.Empty;
    public string? CustomerCode { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public bool IsActive { get; set; } = true;
    public bool RemindersPaused { get; set; }
    public string? Notes { get; set; }
}

public sealed record CustomerListItem(
    int Id, string Name, string? CustomerCode, string? Email, bool IsActive, bool RemindersPaused,
    decimal Outstanding, decimal Overdue, int OpenInvoiceCount);

public sealed record PaymentListItem(int Id, int CustomerId, string CustomerName, DateOnly PaymentDate, decimal Amount, decimal Allocated, string? Reference);

public sealed record ActivityItem(
    int Id, ActivityType Type, string? Outcome, string Notes, DateTimeOffset OccurredAtUtc, DateOnly? NextFollowUpDate,
    int? InvoiceId, string? InvoiceNumber, string? CreatedBy);

public sealed record PromiseItem(
    int Id, int CustomerId, string CustomerName, int? InvoiceId, string? InvoiceNumber, decimal PromisedAmount, DateOnly PromisedDate,
    PromiseStatus Status, PromiseDisplayState DisplayState, string? Notes, DateTimeOffset CreatedAtUtc, string? CreatedBy,
    DateTimeOffset? ResolvedAtUtc, string? ResolvedBy, string? ResolutionNotes);

public sealed record ReminderItem(
    int Id, int CustomerId, string CustomerName, int? InvoiceId, string? InvoiceNumber, string Recipient, string Subject,
    ReminderStatus Status, int AttemptCount, string? LastError, DateTimeOffset CreatedAtUtc, DateTimeOffset? SentAtUtc, string? CreatedBy);

public sealed record CustomerDetail(
    Customer Customer,
    DateOnly AsOfDate,
    IReadOnlyList<InvoiceSummary> Invoices,
    IReadOnlyList<PaymentListItem> Payments,
    IReadOnlyList<PromiseItem> Promises,
    IReadOnlyList<ActivityItem> Activities,
    IReadOnlyList<ReminderItem> Reminders)
{
    public decimal Outstanding => Invoices.Where(i => i.Financials.HasOutstandingBalance).Sum(i => i.Financials.Outstanding);
    public decimal Overdue => Invoices.Where(i => i.Financials.Status == InvoiceStatus.Overdue).Sum(i => i.Financials.Outstanding);
    public decimal Unallocated => Payments.Sum(p => p.Amount - p.Allocated);
}

public sealed class CustomerService
{
    private readonly AppDbContext _db;
    private readonly BusinessService _business;
    private readonly InvoiceQueryService _invoices;
    private readonly ActivityQueries _queries;

    public CustomerService(AppDbContext db, BusinessService business, InvoiceQueryService invoices, ActivityQueries queries)
    {
        _db = db;
        _business = business;
        _invoices = invoices;
        _queries = queries;
    }

    public async Task<PagedResult<CustomerListItem>> ListAsync(string? search, int page, int pageSize, CancellationToken ct)
    {
        var today = await _business.GetTodayAsync(ct);
        var query = _db.Customers.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(c => c.Name.Contains(term) || (c.CustomerCode != null && c.CustomerCode.Contains(term)) || (c.Email != null && c.Email.Contains(term)));
        }

        var total = await query.CountAsync(ct);
        page = Math.Clamp(page, 1, Math.Max(1, (int)Math.Ceiling(total / (double)pageSize)));
        var customers = await query
            .OrderBy(c => c.Name).ThenBy(c => c.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct);

        var ids = customers.Select(c => c.Id).ToList();
        var balances = (await _invoices.GetAsync(q => q.Where(i => ids.Contains(i.CustomerId)), nonZeroBalanceOnly: true, today, ct))
            .Where(i => i.Financials.HasOutstandingBalance)
            .GroupBy(i => i.CustomerId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var items = customers.Select(c =>
        {
            var open = balances.GetValueOrDefault(c.Id) ?? new List<InvoiceSummary>();
            return new CustomerListItem(c.Id, c.Name, c.CustomerCode, c.Email, c.IsActive, c.RemindersPaused,
                open.Sum(i => i.Financials.Outstanding),
                open.Where(i => i.Financials.Status == InvoiceStatus.Overdue).Sum(i => i.Financials.Outstanding),
                open.Count);
        }).ToList();

        return new PagedResult<CustomerListItem>(items, page, pageSize, total);
    }

    public async Task<CustomerDetail?> GetDetailAsync(int id, CancellationToken ct)
    {
        var customer = await _db.Customers.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct);
        if (customer is null)
        {
            return null;
        }

        var today = await _business.GetTodayAsync(ct);
        var invoices = (await _invoices.GetAsync(q => q.Where(i => i.CustomerId == id), nonZeroBalanceOnly: false, today, ct))
            .OrderBy(i => i.Financials.Status == InvoiceStatus.Paid).ThenBy(i => i.DueDate).ThenBy(i => i.InvoiceNumber)
            .ToList();

        return new CustomerDetail(
            customer,
            today,
            invoices,
            await _queries.PaymentsAsync(p => p.CustomerId == id, 100, ct),
            await _queries.PromisesAsync(p => p.CustomerId == id, today, 100, ct),
            await _queries.ActivitiesAsync(a => a.CustomerId == id, 100, ct),
            await _queries.RemindersAsync(r => r.CustomerId == id, 50, ct));
    }

    public async Task<List<(int Id, string Name)>> ListActiveForSelectAsync(CancellationToken ct)
        => (await _db.Customers.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Name).ThenBy(c => c.Id)
                .Select(c => new { c.Id, c.Name, c.CustomerCode }).ToListAsync(ct))
            .Select(c => (c.Id, c.CustomerCode is null ? c.Name : $"{c.Name} ({c.CustomerCode})"))
            .ToList();

    public async Task<ServiceResult> CreateAsync(CustomerInput input, CancellationToken ct)
    {
        var errors = await ValidateAsync(input, existingId: null, ct);
        if (errors.Count > 0)
        {
            return ServiceResult.Fail(errors);
        }

        var customer = new Customer();
        Apply(customer, input);
        _db.Customers.Add(customer);
        await _db.SaveChangesAsync(ct);
        return ServiceResult.Ok(customer.Id);
    }

    public async Task<ServiceResult> UpdateAsync(int id, CustomerInput input, CancellationToken ct)
    {
        var customer = await _db.Customers.SingleOrDefaultAsync(c => c.Id == id, ct);
        if (customer is null)
        {
            return ServiceResult.Missing();
        }

        var errors = await ValidateAsync(input, id, ct);
        if (errors.Count > 0)
        {
            return ServiceResult.Fail(errors);
        }

        Apply(customer, input);
        await _db.SaveChangesAsync(ct);
        return ServiceResult.Ok(id);
    }

    public async Task<ServiceResult> SetRemindersPausedAsync(int id, bool paused, CancellationToken ct)
    {
        var customer = await _db.Customers.SingleOrDefaultAsync(c => c.Id == id, ct);
        if (customer is null)
        {
            return ServiceResult.Missing();
        }

        customer.RemindersPaused = paused;
        await _db.SaveChangesAsync(ct);
        return ServiceResult.Ok(id);
    }

    private static void Apply(Customer customer, CustomerInput input)
    {
        customer.Name = input.Name.Trim();
        customer.CustomerCode = string.IsNullOrWhiteSpace(input.CustomerCode) ? null : input.CustomerCode.Trim();
        customer.Email = string.IsNullOrWhiteSpace(input.Email) ? null : input.Email.Trim();
        customer.Phone = string.IsNullOrWhiteSpace(input.Phone) ? null : input.Phone.Trim();
        customer.IsActive = input.IsActive;
        customer.RemindersPaused = input.RemindersPaused;
        customer.Notes = string.IsNullOrWhiteSpace(input.Notes) ? null : input.Notes.Trim();
    }

    private async Task<List<ServiceError>> ValidateAsync(CustomerInput input, int? existingId, CancellationToken ct)
    {
        var errors = new List<ServiceError>();
        var name = input.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            errors.Add(new(nameof(input.Name), "Customer name is required."));
        }
        else if (name.Length > Customer.NameMaxLength)
        {
            errors.Add(new(nameof(input.Name), $"Customer name must be at most {Customer.NameMaxLength} characters."));
        }

        var code = input.CustomerCode?.Trim();
        if (!string.IsNullOrEmpty(code))
        {
            if (code.Length > Customer.CodeMaxLength)
            {
                errors.Add(new(nameof(input.CustomerCode), $"Customer code must be at most {Customer.CodeMaxLength} characters."));
            }
            else if (await _db.Customers.AnyAsync(c => c.CustomerCode == code && c.Id != existingId, ct))
            {
                errors.Add(new(nameof(input.CustomerCode), "Another customer already uses this code."));
            }
        }

        if (!string.IsNullOrWhiteSpace(input.Email) && !ImportValueParser.IsValidEmail(input.Email.Trim()))
        {
            errors.Add(new(nameof(input.Email), "Enter a valid email address."));
        }

        if (input.Phone?.Trim().Length > Customer.PhoneMaxLength)
        {
            errors.Add(new(nameof(input.Phone), $"Phone must be at most {Customer.PhoneMaxLength} characters."));
        }

        if (input.Notes?.Trim().Length > Customer.NotesMaxLength)
        {
            errors.Add(new(nameof(input.Notes), $"Notes must be at most {Customer.NotesMaxLength} characters."));
        }

        return errors;
    }
}
