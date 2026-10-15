using Collections.Core.Entities;
using Collections.Core.Financial;
using Collections.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Collections.Web.Services;

public sealed record MissedPromiseItem(int Id, int CustomerId, string CustomerName, string? InvoiceNumber, decimal PromisedAmount, DateOnly PromisedDate);

public sealed record DashboardData(
    DateOnly AsOfDate,
    DashboardTotals Totals,
    int CustomerCount,
    int InvoiceCount,
    DateTimeOffset? LastImportUtc,
    IReadOnlyList<MissedPromiseItem> MissedPromises,
    string TimeZoneId);

public sealed class DashboardService
{
    private readonly AppDbContext _db;
    private readonly BusinessService _business;
    private readonly InvoiceQueryService _invoices;

    public DashboardService(AppDbContext db, BusinessService business, InvoiceQueryService invoices)
    {
        _db = db;
        _business = business;
        _invoices = invoices;
    }

    public async Task<DashboardData> GetAsync(CancellationToken ct)
    {
        var business = await _business.GetCurrentAsync(ct);
        var today = await _business.GetTodayAsync(ct);

        var invoices = await _invoices.GetAsync(null, nonZeroBalanceOnly: true, today, ct);

        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);
        var payments = await _db.Payments.AsNoTracking()
            .Where(p => p.PaymentDate >= monthStart && p.PaymentDate <= monthEnd)
            .Select(p => new PaymentAmount(p.PaymentDate, p.Amount))
            .ToListAsync(ct);

        var promises = await _db.PromisesToPay.AsNoTracking()
            .Where(p => p.Status == PromiseStatus.Open)
            .Select(p => new PromiseSnapshot(p.Status, p.PromisedDate))
            .ToListAsync(ct);

        var totals = DashboardCalculator.Compute(invoices.Select(i => i.Financials), payments, promises, today);

        var missed = await _db.PromisesToPay.AsNoTracking()
            .Where(p => p.Status == PromiseStatus.Open && p.PromisedDate < today)
            .OrderBy(p => p.PromisedDate).ThenBy(p => p.Id)
            .Take(10)
            .Select(p => new MissedPromiseItem(p.Id, p.CustomerId, p.Customer!.Name, p.Invoice != null ? p.Invoice.InvoiceNumber : null, p.PromisedAmount, p.PromisedDate))
            .ToListAsync(ct);

        var lastImport = await _db.ImportBatches.AsNoTracking()
            .Where(b => b.Status == ImportBatchStatus.Committed)
            .MaxAsync(b => (DateTimeOffset?)b.CompletedAtUtc, ct);

        return new DashboardData(
            today,
            totals,
            await _db.Customers.CountAsync(ct),
            await _db.Invoices.CountAsync(ct),
            lastImport,
            missed,
            business.TimeZoneId);
    }
}
