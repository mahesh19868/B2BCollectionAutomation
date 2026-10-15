using Collections.Core.Entities;
using Collections.Core.Financial;
using Collections.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Collections.Web.Services;

public enum WorklistStatus
{
    AllOutstanding = 0,
    Overdue = 1,
    DueToday = 2,
    Upcoming = 3,
}

public enum WorklistPromiseFilter
{
    Any = 0,
    Missed = 1,
    Open = 2,
    None = 3,
}

public class WorklistFilter
{
    public WorklistStatus Status { get; set; } = WorklistStatus.AllOutstanding;
    public AgeingBucket? Bucket { get; set; }
    public string? Search { get; set; }
    public decimal? MinAmount { get; set; }
    public WorklistPromiseFilter Promise { get; set; } = WorklistPromiseFilter.Any;

    /// <summary>Hide invoices whose next follow-up date is scheduled after today.</summary>
    public bool DueForFollowUpOnly { get; set; }
    public int Page { get; set; } = 1;
}

public sealed record WorklistPromise(DateOnly PromisedDate, decimal Amount, PromiseDisplayState State, bool IsAccountLevel);

public sealed record WorklistItem(
    InvoiceSummary Invoice,
    int PriorityGroup,
    DateTimeOffset? LastFollowUpUtc,
    DateOnly? NextFollowUpDate,
    WorklistPromise? Promise,
    bool HasMissedPromise,
    ReminderStatus? LastReminderStatus,
    DateTimeOffset? LastReminderUtc);

public sealed record WorklistResult(PagedResult<WorklistItem> Page, DateOnly AsOfDate, decimal FilteredOutstanding, string TimeZoneId);

/// <summary>Daily follow-up worklist: outstanding invoices with follow-up, promise and reminder context, in explainable priority order.</summary>
public sealed class WorklistService
{
    public const int PageSize = 50;

    private readonly AppDbContext _db;
    private readonly BusinessService _business;
    private readonly InvoiceQueryService _invoices;

    public WorklistService(AppDbContext db, BusinessService business, InvoiceQueryService invoices)
    {
        _db = db;
        _business = business;
        _invoices = invoices;
    }

    public async Task<WorklistResult> GetAsync(WorklistFilter filter, CancellationToken ct)
    {
        var business = await _business.GetCurrentAsync(ct);
        var today = await _business.GetTodayAsync(ct);
        var search = filter.Search?.Trim();

        var invoices = (await _invoices.GetAsync(q => string.IsNullOrEmpty(search)
                    ? q
                    : q.Where(i => i.InvoiceNumber.Contains(search) || i.Customer!.Name.Contains(search) ||
                                   (i.Customer.CustomerCode != null && i.Customer.CustomerCode.Contains(search))),
                nonZeroBalanceOnly: true, today, ct))
            .Where(i => i.Financials.HasOutstandingBalance)
            .ToList();

        var customerIds = invoices.Select(i => i.CustomerId).Distinct().ToList();
        var invoiceIds = invoices.Select(i => i.Id).ToList();

        var activities = await _db.FollowUpActivities.AsNoTracking()
            .Where(a => customerIds.Contains(a.CustomerId) && a.ActivityType != ActivityType.System)
            .Select(a => new { a.Id, a.CustomerId, a.InvoiceId, a.OccurredAtUtc, a.NextFollowUpDate })
            .ToListAsync(ct);

        var promises = await _db.PromisesToPay.AsNoTracking()
            .Where(p => customerIds.Contains(p.CustomerId) && p.Status == PromiseStatus.Open)
            .Select(p => new { p.Id, p.CustomerId, p.InvoiceId, p.PromisedDate, p.PromisedAmount, p.Status })
            .ToListAsync(ct);

        var reminders = await _db.ReminderLogs.AsNoTracking()
            .Where(r => r.InvoiceId != null && invoiceIds.Contains(r.InvoiceId.Value))
            .Select(r => new { r.Id, InvoiceId = r.InvoiceId!.Value, r.Status, r.CreatedAtUtc })
            .ToListAsync(ct);

        var activitiesByCustomer = activities.ToLookup(a => a.CustomerId);
        var promisesByCustomer = promises.ToLookup(p => p.CustomerId);
        var lastByCustomer = activities.GroupBy(a => a.CustomerId).ToDictionary(g => g.Key, g => g.Max(a => a.OccurredAtUtc));
        var latestReminder = reminders.GroupBy(r => r.InvoiceId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.CreatedAtUtc).ThenByDescending(r => r.Id).First());

        var items = invoices.Select(inv =>
        {
            // Most recent user activity for this invoice or for the account as a whole decides the next follow-up date.
            var latestRelevant = activitiesByCustomer[inv.CustomerId]
                .Where(a => a.InvoiceId == inv.Id || a.InvoiceId == null)
                .OrderByDescending(a => a.OccurredAtUtc).ThenByDescending(a => a.Id)
                .FirstOrDefault();

            var relevantPromises = promisesByCustomer[inv.CustomerId]
                .Where(p => p.InvoiceId == inv.Id || p.InvoiceId == null)
                .OrderBy(p => p.PromisedDate).ThenBy(p => p.Id)
                .ToList();
            var hasMissed = relevantPromises.Any(p => PromiseRules.IsOverdue(p.Status, p.PromisedDate, today));
            var shown = relevantPromises.FirstOrDefault();

            latestReminder.TryGetValue(inv.Id, out var reminder);

            var candidate = new WorklistCandidate(inv.Id, inv.InvoiceNumber, inv.Financials, hasMissed);
            return new WorklistItem(
                inv,
                WorklistPrioritizer.PriorityGroup(candidate),
                lastByCustomer.TryGetValue(inv.CustomerId, out var last) ? last : null,
                latestRelevant?.NextFollowUpDate,
                shown is null ? null : new WorklistPromise(shown.PromisedDate, shown.PromisedAmount,
                    PromiseRules.GetDisplayState(shown.Status, shown.PromisedDate, today), shown.InvoiceId is null),
                hasMissed,
                reminder?.Status,
                reminder?.CreatedAtUtc);
        });

        items = filter.Status switch
        {
            WorklistStatus.Overdue => items.Where(i => i.Invoice.Financials.Status == InvoiceStatus.Overdue),
            WorklistStatus.DueToday => items.Where(i => i.Invoice.Financials.Status == InvoiceStatus.DueToday),
            WorklistStatus.Upcoming => items.Where(i => i.Invoice.Financials.Status == InvoiceStatus.NotDue),
            _ => items,
        };

        if (filter.Bucket is { } bucket)
        {
            items = items.Where(i => i.Invoice.Financials.Bucket == bucket);
        }

        if (filter.MinAmount is { } min)
        {
            items = items.Where(i => i.Invoice.Financials.Outstanding >= min);
        }

        items = filter.Promise switch
        {
            WorklistPromiseFilter.Missed => items.Where(i => i.HasMissedPromise),
            WorklistPromiseFilter.Open => items.Where(i => i.Promise is not null),
            WorklistPromiseFilter.None => items.Where(i => i.Promise is null),
            _ => items,
        };

        if (filter.DueForFollowUpOnly)
        {
            items = items.Where(i => i.NextFollowUpDate is null || i.NextFollowUpDate <= today);
        }

        var sorted = WorklistPrioritizer.Sort(items.ToList(),
            i => new WorklistCandidate(i.Invoice.Id, i.Invoice.InvoiceNumber, i.Invoice.Financials, i.HasMissedPromise));

        var totalPages = Math.Max(1, (int)Math.Ceiling(sorted.Count / (double)PageSize));
        var page = Math.Clamp(filter.Page, 1, totalPages);
        var pageItems = sorted.Skip((page - 1) * PageSize).Take(PageSize).ToList();

        return new WorklistResult(
            new PagedResult<WorklistItem>(pageItems, page, PageSize, sorted.Count),
            today,
            sorted.Sum(i => i.Invoice.Financials.Outstanding),
            business.TimeZoneId);
    }
}
