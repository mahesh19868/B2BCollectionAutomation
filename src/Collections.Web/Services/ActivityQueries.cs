using System.Linq.Expressions;
using Collections.Core.Entities;
using Collections.Core.Financial;
using Collections.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Collections.Web.Services;

/// <summary>Shared read-only projections for history lists shown on customer and invoice pages.</summary>
public sealed class ActivityQueries
{
    private readonly AppDbContext _db;

    public ActivityQueries(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<PaymentListItem>> PaymentsAsync(Expression<Func<Payment, bool>> filter, int take, CancellationToken ct)
        => await _db.Payments.AsNoTracking().Where(filter)
            .OrderByDescending(p => p.PaymentDate).ThenByDescending(p => p.Id)
            .Take(take)
            .Select(p => new PaymentListItem(p.Id, p.CustomerId, p.Customer!.Name, p.PaymentDate, p.Amount,
                p.Allocations.Sum(a => (decimal?)a.AllocatedAmount) ?? 0m, p.Reference))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<ActivityItem>> ActivitiesAsync(Expression<Func<FollowUpActivity, bool>> filter, int take, CancellationToken ct)
    {
        var rows = await _db.FollowUpActivities.AsNoTracking().Where(filter)
            .OrderByDescending(a => a.OccurredAtUtc).ThenByDescending(a => a.Id)
            .Take(take)
            .Select(a => new
            {
                a.Id, a.ActivityType, a.Outcome, a.Notes, a.OccurredAtUtc, a.NextFollowUpDate, a.InvoiceId,
                InvoiceNumber = a.Invoice != null ? a.Invoice.InvoiceNumber : null,
                a.CreatedByUserId,
            })
            .ToListAsync(ct);

        var users = await UserNamesAsync(rows.Select(r => r.CreatedByUserId), ct);
        return rows.Select(a => new ActivityItem(a.Id, a.ActivityType, a.Outcome, a.Notes, a.OccurredAtUtc, a.NextFollowUpDate,
            a.InvoiceId, a.InvoiceNumber, users.GetValueOrDefault(a.CreatedByUserId ?? string.Empty))).ToList();
    }

    public async Task<IReadOnlyList<PromiseItem>> PromisesAsync(Expression<Func<PromiseToPay, bool>> filter, DateOnly asOfDate, int take, CancellationToken ct)
    {
        var rows = await _db.PromisesToPay.AsNoTracking().Where(filter)
            .OrderByDescending(p => p.Status == PromiseStatus.Open).ThenBy(p => p.PromisedDate).ThenBy(p => p.Id)
            .Take(take)
            .Select(p => new
            {
                p.Id, p.CustomerId, CustomerName = p.Customer!.Name, p.InvoiceId,
                InvoiceNumber = p.Invoice != null ? p.Invoice.InvoiceNumber : null,
                p.PromisedAmount, p.PromisedDate, p.Status, p.Notes, p.CreatedAtUtc, p.CreatedByUserId,
                p.ResolvedAtUtc, p.ResolvedByUserId, p.ResolutionNotes,
            })
            .ToListAsync(ct);

        var users = await UserNamesAsync(rows.SelectMany(r => new[] { r.CreatedByUserId, r.ResolvedByUserId }), ct);
        return rows.Select(p => new PromiseItem(p.Id, p.CustomerId, p.CustomerName, p.InvoiceId, p.InvoiceNumber, p.PromisedAmount,
            p.PromisedDate, p.Status, PromiseRules.GetDisplayState(p.Status, p.PromisedDate, asOfDate), p.Notes, p.CreatedAtUtc,
            users.GetValueOrDefault(p.CreatedByUserId ?? string.Empty), p.ResolvedAtUtc,
            users.GetValueOrDefault(p.ResolvedByUserId ?? string.Empty), p.ResolutionNotes)).ToList();
    }

    public async Task<IReadOnlyList<ReminderItem>> RemindersAsync(Expression<Func<ReminderLog, bool>> filter, int take, CancellationToken ct)
    {
        var rows = await _db.ReminderLogs.AsNoTracking().Where(filter)
            .OrderByDescending(r => r.CreatedAtUtc).ThenByDescending(r => r.Id)
            .Take(take)
            .Select(r => new
            {
                r.Id, r.CustomerId, CustomerName = r.Customer!.Name, r.InvoiceId,
                InvoiceNumber = r.Invoice != null ? r.Invoice.InvoiceNumber : null,
                r.Recipient, r.Subject, r.Status, r.AttemptCount, r.LastError, r.CreatedAtUtc, r.SentAtUtc, r.CreatedByUserId,
            })
            .ToListAsync(ct);

        var users = await UserNamesAsync(rows.Select(r => r.CreatedByUserId), ct);
        return rows.Select(r => new ReminderItem(r.Id, r.CustomerId, r.CustomerName, r.InvoiceId, r.InvoiceNumber, r.Recipient, r.Subject,
            r.Status, r.AttemptCount, r.LastError, r.CreatedAtUtc, r.SentAtUtc, users.GetValueOrDefault(r.CreatedByUserId ?? string.Empty))).ToList();
    }

    /// <summary>Resolves user ids to emails, restricted to users of the current business.</summary>
    private async Task<Dictionary<string, string>> UserNamesAsync(IEnumerable<string?> ids, CancellationToken ct)
    {
        var distinct = ids.Where(i => i is not null).Select(i => i!).Distinct().ToList();
        if (distinct.Count == 0)
        {
            return new Dictionary<string, string>();
        }

        var businessId = _db.CurrentBusinessId;
        return await _db.Users.AsNoTracking()
            .Where(u => distinct.Contains(u.Id) && u.BusinessId == businessId)
            .ToDictionaryAsync(u => u.Id, u => u.Email ?? u.UserName ?? "user", ct);
    }
}
