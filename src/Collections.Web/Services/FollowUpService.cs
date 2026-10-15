using Collections.Core.Entities;
using Collections.Core.Financial;
using Collections.Core.Formatting;
using Collections.Web.Data;
using Collections.Web.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Collections.Web.Services;

public class FollowUpInput
{
    public int CustomerId { get; set; }
    public int? InvoiceId { get; set; }
    public ActivityType ActivityType { get; set; } = ActivityType.Call;
    public string? Outcome { get; set; }
    public string? Notes { get; set; }
    public DateOnly? NextFollowUpDate { get; set; }
}

public class PromiseInput
{
    public int CustomerId { get; set; }
    public int? InvoiceId { get; set; }
    public decimal? PromisedAmount { get; set; }
    public DateOnly? PromisedDate { get; set; }
    public string? Notes { get; set; }
}

public enum PromiseListView
{
    Open = 0,
    Missed = 1,
    Upcoming = 2,
    Resolved = 3,
    All = 4,
}

/// <summary>Manual follow-up notes and promises to pay. A promise never changes invoice balances.</summary>
public sealed class FollowUpService
{
    private readonly AppDbContext _db;
    private readonly BusinessService _business;
    private readonly InvoiceQueryService _invoices;
    private readonly ActivityQueries _queries;
    private readonly ICurrentBusinessContext _context;
    private readonly TimeProvider _time;

    public FollowUpService(AppDbContext db, BusinessService business, InvoiceQueryService invoices, ActivityQueries queries,
        ICurrentBusinessContext context, TimeProvider time)
    {
        _db = db;
        _business = business;
        _invoices = invoices;
        _queries = queries;
        _context = context;
        _time = time;
    }

    /// <summary>Customer name and (optional) invoice number, verified to belong to the current business and to each other.</summary>
    public async Task<(string CustomerName, string? InvoiceNumber)?> GetTargetAsync(int customerId, int? invoiceId, CancellationToken ct)
    {
        var customer = await _db.Customers.AsNoTracking().Where(c => c.Id == customerId).Select(c => c.Name).SingleOrDefaultAsync(ct);
        if (customer is null)
        {
            return null;
        }

        if (invoiceId is null)
        {
            return (customer, null);
        }

        var number = await _db.Invoices.AsNoTracking().Where(i => i.Id == invoiceId && i.CustomerId == customerId)
            .Select(i => i.InvoiceNumber).SingleOrDefaultAsync(ct);
        return number is null ? null : (customer, number);
    }

    public async Task<ServiceResult> AddActivityAsync(FollowUpInput input, CancellationToken ct)
    {
        if (await GetTargetAsync(input.CustomerId, input.InvoiceId, ct) is null)
        {
            return ServiceResult.Missing();
        }

        var today = await _business.GetTodayAsync(ct);
        var errors = new List<ServiceError>();

        if (input.ActivityType is not (ActivityType.Call or ActivityType.Email or ActivityType.Note or ActivityType.Other))
        {
            errors.Add(new(nameof(input.ActivityType), "Choose an activity type."));
        }

        var notes = input.Notes?.Trim() ?? string.Empty;
        if (notes.Length == 0)
        {
            errors.Add(new(nameof(input.Notes), "Notes are required."));
        }
        else if (notes.Length > FollowUpActivity.NotesMaxLength)
        {
            errors.Add(new(nameof(input.Notes), $"Notes must be at most {FollowUpActivity.NotesMaxLength} characters."));
        }

        var outcome = string.IsNullOrWhiteSpace(input.Outcome) ? null : input.Outcome.Trim();
        if (outcome?.Length > FollowUpActivity.OutcomeMaxLength)
        {
            errors.Add(new(nameof(input.Outcome), $"Outcome must be at most {FollowUpActivity.OutcomeMaxLength} characters."));
        }

        if (input.NextFollowUpDate is { } next && next < today)
        {
            errors.Add(new(nameof(input.NextFollowUpDate), "Next follow-up date cannot be in the past."));
        }

        if (errors.Count > 0)
        {
            return ServiceResult.Fail(errors);
        }

        var now = _time.GetUtcNow();
        var activity = new FollowUpActivity
        {
            CustomerId = input.CustomerId,
            InvoiceId = input.InvoiceId,
            ActivityType = input.ActivityType,
            Outcome = outcome,
            Notes = notes,
            NextFollowUpDate = input.NextFollowUpDate,
            OccurredAtUtc = now,
            CreatedAtUtc = now,
            CreatedByUserId = _context.UserId,
        };
        _db.FollowUpActivities.Add(activity);
        await _db.SaveChangesAsync(ct);
        return ServiceResult.Ok(activity.Id);
    }

    public async Task<ServiceResult> AddPromiseAsync(PromiseInput input, CancellationToken ct)
    {
        if (await GetTargetAsync(input.CustomerId, input.InvoiceId, ct) is null)
        {
            return ServiceResult.Missing();
        }

        var today = await _business.GetTodayAsync(ct);
        var errors = new List<ServiceError>();

        if (input.PromisedAmount is null)
        {
            errors.Add(new(nameof(input.PromisedAmount), "Promised amount is required."));
        }
        else if (MoneyRules.ValidatePositiveAmount(input.PromisedAmount.Value) is { } amountError)
        {
            errors.Add(new(nameof(input.PromisedAmount), amountError));
        }

        if (input.PromisedDate is null)
        {
            errors.Add(new(nameof(input.PromisedDate), "Promised date is required."));
        }
        else if (input.PromisedDate < today)
        {
            errors.Add(new(nameof(input.PromisedDate), "Promised date cannot be in the past."));
        }

        var notes = string.IsNullOrWhiteSpace(input.Notes) ? null : input.Notes.Trim();
        if (notes?.Length > PromiseToPay.NotesMaxLength)
        {
            errors.Add(new(nameof(input.Notes), $"Notes must be at most {PromiseToPay.NotesMaxLength} characters."));
        }

        // Promise cannot exceed what is owed (invoice-level: that invoice; account-level: the customer's total).
        if (input.PromisedAmount is { } amount && errors.Count == 0)
        {
            var open = await _invoices.GetAsync(
                q => input.InvoiceId is { } invoiceId ? q.Where(i => i.Id == invoiceId) : q.Where(i => i.CustomerId == input.CustomerId),
                nonZeroBalanceOnly: true, today, ct);
            var owed = open.Where(i => i.Financials.HasOutstandingBalance).Sum(i => i.Financials.Outstanding);
            if (owed <= 0)
            {
                errors.Add(new(nameof(input.PromisedAmount), "There is no outstanding balance to promise against."));
            }
            else if (amount > owed)
            {
                errors.Add(new(nameof(input.PromisedAmount), $"Promised amount cannot exceed the outstanding balance of {MoneyFormat.Inr(owed)}."));
            }
        }

        if (errors.Count > 0)
        {
            return ServiceResult.Fail(errors);
        }

        var promise = new PromiseToPay
        {
            CustomerId = input.CustomerId,
            InvoiceId = input.InvoiceId,
            PromisedAmount = input.PromisedAmount!.Value,
            PromisedDate = input.PromisedDate!.Value,
            Status = PromiseStatus.Open,
            Notes = notes,
            CreatedAtUtc = _time.GetUtcNow(),
            CreatedByUserId = _context.UserId,
        };
        _db.PromisesToPay.Add(promise);
        await _db.SaveChangesAsync(ct);
        return ServiceResult.Ok(promise.Id);
    }

    public async Task<PromiseItem?> GetPromiseAsync(int id, CancellationToken ct)
    {
        var today = await _business.GetTodayAsync(ct);
        return (await _queries.PromisesAsync(p => p.Id == id, today, 1, ct)).SingleOrDefault();
    }

    /// <summary>Records the user-confirmed outcome of an open promise. A resolved promise is never changed again.</summary>
    public async Task<ServiceResult> ResolvePromiseAsync(int id, PromiseStatus outcome, string? notes, CancellationToken ct)
    {
        var promise = await _db.PromisesToPay.AsNoTracking().Include(p => p.Invoice).SingleOrDefaultAsync(p => p.Id == id, ct);
        if (promise is null)
        {
            return ServiceResult.Missing();
        }

        if (!PromiseRules.CanResolveTo(promise.Status, outcome))
        {
            return ServiceResult.Fail(string.Empty, promise.Status == PromiseStatus.Open
                ? "Choose Kept, Missed or Cancelled."
                : $"This promise was already marked {promise.Status} and cannot be changed.");
        }

        var cleanNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        if (cleanNotes?.Length > PromiseToPay.NotesMaxLength)
        {
            return ServiceResult.Fail("ResolutionNotes", $"Notes must be at most {PromiseToPay.NotesMaxLength} characters.");
        }

        var now = _time.GetUtcNow();
        var userId = _context.UserId;

        // Conditional update: only an Open promise can be resolved, so two users cannot record different outcomes.
        var updated = await _db.PromisesToPay
            .Where(p => p.Id == id && p.Status == PromiseStatus.Open)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, outcome)
                .SetProperty(p => p.ResolvedAtUtc, now)
                .SetProperty(p => p.ResolvedByUserId, userId)
                .SetProperty(p => p.ResolutionNotes, cleanNotes), ct);
        if (updated == 0)
        {
            return ServiceResult.Fail(string.Empty, "This promise was resolved by someone else. Refresh to see its status.");
        }

        var target = promise.Invoice is null ? "account" : $"invoice {promise.Invoice.InvoiceNumber}";
        _db.AddSystemNote(promise.CustomerId, promise.InvoiceId,
            $"Promise of {MoneyFormat.Inr(promise.PromisedAmount)} for {DateFormat.Display(promise.PromisedDate)} ({target}) marked {outcome}{(cleanNotes is null ? "" : $": {cleanNotes}")}.",
            userId, now);
        await _db.SaveChangesAsync(ct);

        return ServiceResult.Ok(promise.CustomerId);
    }

    public async Task<(IReadOnlyList<PromiseItem> Items, DateOnly AsOf)> ListPromisesAsync(PromiseListView view, CancellationToken ct)
    {
        var today = await _business.GetTodayAsync(ct);
        IReadOnlyList<PromiseItem> items = view switch
        {
            PromiseListView.Missed => await _queries.PromisesAsync(p => p.Status == PromiseStatus.Open && p.PromisedDate < today, today, 500, ct),
            PromiseListView.Upcoming => await _queries.PromisesAsync(p => p.Status == PromiseStatus.Open && p.PromisedDate >= today, today, 500, ct),
            PromiseListView.Resolved => await _queries.PromisesAsync(p => p.Status != PromiseStatus.Open, today, 500, ct),
            PromiseListView.All => await _queries.PromisesAsync(p => true, today, 500, ct),
            _ => await _queries.PromisesAsync(p => p.Status == PromiseStatus.Open, today, 500, ct),
        };
        return (items, today);
    }
}
