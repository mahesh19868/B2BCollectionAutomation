using Collections.Core.Entities;
using Collections.Core.Financial;
using Collections.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Collections.Web.Services;

public sealed record InvoiceSummary(
    int Id,
    string InvoiceNumber,
    int CustomerId,
    string CustomerName,
    string? CustomerCode,
    string? CustomerEmail,
    bool CustomerIsActive,
    bool CustomerRemindersPaused,
    DateOnly InvoiceDate,
    DateOnly DueDate,
    bool IsDisputed,
    bool RemindersPaused,
    InvoiceFinancials Financials);

/// <summary>
/// Loads invoices with their allocated payment totals and runs the shared <see cref="InvoiceCalculator"/>.
/// Every page that shows a balance (dashboard, lists, worklist, reminders) goes through here.
/// </summary>
public sealed class InvoiceQueryService
{
    private readonly AppDbContext _db;

    public InvoiceQueryService(AppDbContext db)
    {
        _db = db;
    }

    /// <param name="filter">Optional extra SQL filter (already tenant-scoped by the context).</param>
    /// <param name="nonZeroBalanceOnly">Excludes fully paid invoices in SQL. Invalid (negative) balances are kept so they can be reported.</param>
    public async Task<List<InvoiceSummary>> GetAsync(
        Func<IQueryable<Invoice>, IQueryable<Invoice>>? filter,
        bool nonZeroBalanceOnly,
        DateOnly asOfDate,
        CancellationToken ct)
    {
        var query = _db.Invoices.AsNoTracking();
        if (filter is not null)
        {
            query = filter(query);
        }

        if (nonZeroBalanceOnly)
        {
            query = query.Where(i => i.InvoiceAmount != (i.Allocations.Sum(a => (decimal?)a.AllocatedAmount) ?? 0m));
        }

        var rows = await query
            .Select(i => new
            {
                i.Id,
                i.InvoiceNumber,
                i.CustomerId,
                CustomerName = i.Customer!.Name,
                i.Customer.CustomerCode,
                CustomerEmail = i.Customer.Email,
                CustomerIsActive = i.Customer.IsActive,
                CustomerRemindersPaused = i.Customer.RemindersPaused,
                i.InvoiceDate,
                i.DueDate,
                i.InvoiceAmount,
                Allocated = i.Allocations.Sum(a => (decimal?)a.AllocatedAmount) ?? 0m,
                i.IsDisputed,
                i.RemindersPaused,
            })
            .ToListAsync(ct);

        return rows.Select(r => new InvoiceSummary(
                r.Id, r.InvoiceNumber, r.CustomerId, r.CustomerName, r.CustomerCode, r.CustomerEmail, r.CustomerIsActive,
                r.CustomerRemindersPaused, r.InvoiceDate, r.DueDate, r.IsDisputed, r.RemindersPaused,
                InvoiceCalculator.Calculate(r.InvoiceAmount, r.Allocated, r.DueDate, asOfDate)))
            .ToList();
    }

    public async Task<InvoiceSummary?> GetOneAsync(int invoiceId, DateOnly asOfDate, CancellationToken ct)
        => (await GetAsync(q => q.Where(i => i.Id == invoiceId), nonZeroBalanceOnly: false, asOfDate, ct)).SingleOrDefault();
}
