using Collections.Core.Entities;
using Collections.Core.Reminders;
using Collections.Web.Data;
using Collections.Web.Email;
using Collections.Web.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Collections.Web.Services;

public sealed record ReminderPreview(
    InvoiceSummary Invoice,
    string? Recipient,
    string Subject,
    string Body,
    string FromDisplayName,
    string? ReplyTo,
    IReadOnlyList<string> BlockingReasons,
    string IdempotencyKey)
{
    public bool CanSend => BlockingReasons.Count == 0;
}

public sealed record ReminderSendResult(bool Sent, int? LogId, IReadOnlyList<string> Errors, bool NotFound = false);

/// <summary>
/// Manual, previewed email reminders for a single invoice. Every attempt is recorded in the reminder log.
/// Safety rules: eligibility is re-checked at send time; an idempotency key prevents double sends;
/// a cooldown prevents repeated reminders; reminders are never sent automatically.
/// </summary>
public sealed class ReminderService
{
    public const int HistoryPageSize = 50;

    private readonly AppDbContext _db;
    private readonly BusinessService _business;
    private readonly InvoiceQueryService _invoices;
    private readonly ActivityQueries _queries;
    private readonly IEmailSender _sender;
    private readonly ICurrentBusinessContext _context;
    private readonly TimeProvider _time;

    public ReminderService(AppDbContext db, BusinessService business, InvoiceQueryService invoices, ActivityQueries queries,
        IEmailSender sender, ICurrentBusinessContext context, TimeProvider time)
    {
        _db = db;
        _business = business;
        _invoices = invoices;
        _queries = queries;
        _sender = sender;
        _context = context;
        _time = time;
    }

    public async Task<ReminderPreview?> PreviewAsync(int invoiceId, CancellationToken ct)
    {
        var business = await _business.GetCurrentAsync(ct);
        var today = await _business.GetTodayAsync(ct);
        var invoice = await _invoices.GetOneAsync(invoiceId, today, ct);
        if (invoice is null)
        {
            return null;
        }

        var values = ReminderEligibility.BuildTemplateValues(
            invoice.CustomerName, invoice.InvoiceNumber, invoice.InvoiceDate, invoice.DueDate,
            invoice.Financials.InvoiceAmount, invoice.Financials.Outstanding, business.Name, BusinessContact(business));

        return new ReminderPreview(
            invoice,
            invoice.CustomerEmail,
            ReminderTemplateRenderer.RenderSubject(business.ReminderSubjectTemplate, values),
            ReminderTemplateRenderer.Render(business.ReminderBodyTemplate, values),
            business.Name,
            business.ContactEmail,
            await GetBlockingReasonsAsync(business, invoice, ct),
            Guid.NewGuid().ToString("N"));
    }

    public async Task<ReminderSendResult> SendAsync(int invoiceId, string idempotencyKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 64)
        {
            return new ReminderSendResult(false, null, new[] { "The reminder form has expired. Open the preview again." });
        }

        // A repeated submission with the same key returns the original attempt instead of sending again.
        var existing = await _db.ReminderLogs.AsNoTracking().Where(r => r.IdempotencyKey == idempotencyKey)
            .Select(r => new { r.Id, r.Status }).SingleOrDefaultAsync(ct);
        if (existing is not null)
        {
            return new ReminderSendResult(existing.Status == ReminderStatus.Sent, existing.Id, Array.Empty<string>());
        }

        var preview = await PreviewAsync(invoiceId, ct);
        if (preview is null)
        {
            return new ReminderSendResult(false, null, new[] { "Invoice not found." }, NotFound: true);
        }

        if (!preview.CanSend)
        {
            return new ReminderSendResult(false, null, preview.BlockingReasons);
        }

        var log = new ReminderLog
        {
            CustomerId = preview.Invoice.CustomerId,
            InvoiceId = preview.Invoice.Id,
            Channel = ReminderChannel.Email,
            TemplateKey = ReminderTemplateDefaults.TemplateKey,
            IdempotencyKey = idempotencyKey,
            Recipient = preview.Recipient!,
            Subject = preview.Subject,
            Body = preview.Body,
            OutstandingAmount = preview.Invoice.Financials.Outstanding,
            Status = ReminderStatus.Queued,
            AttemptCount = 1,
            CreatedAtUtc = _time.GetUtcNow(),
            CreatedByUserId = _context.UserId,
        };
        _db.ReminderLogs.Add(log);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Unique (BusinessId, IdempotencyKey): a concurrent identical submission already created the log.
            _db.ChangeTracker.Clear();
            var winner = await _db.ReminderLogs.AsNoTracking().Where(r => r.IdempotencyKey == idempotencyKey).Select(r => (int?)r.Id).SingleOrDefaultAsync(ct);
            if (winner is not null)
            {
                return new ReminderSendResult(false, winner, Array.Empty<string>());
            }

            throw;
        }

        return await DeliverAsync(log.Id, preview.FromDisplayName, preview.ReplyTo, ct);
    }

    /// <summary>Retries a failed reminder with the same content, if the invoice is still eligible and its balance is unchanged.</summary>
    public async Task<ReminderSendResult> RetryAsync(int logId, CancellationToken ct)
    {
        var log = await _db.ReminderLogs.AsNoTracking().SingleOrDefaultAsync(r => r.Id == logId, ct);
        if (log is null)
        {
            return new ReminderSendResult(false, null, new[] { "Reminder not found." }, NotFound: true);
        }

        if (log.Status != ReminderStatus.Failed || log.InvoiceId is null)
        {
            return new ReminderSendResult(false, logId, new[] { "Only failed reminders can be retried." });
        }

        var business = await _business.GetCurrentAsync(ct);
        var today = await _business.GetTodayAsync(ct);
        var invoice = await _invoices.GetOneAsync(log.InvoiceId.Value, today, ct);
        if (invoice is null)
        {
            return new ReminderSendResult(false, logId, new[] { "Invoice not found." });
        }

        var reasons = (await GetBlockingReasonsAsync(business, invoice, ct)).ToList();
        if (invoice.Financials.Outstanding != log.OutstandingAmount)
        {
            reasons.Add("The invoice balance has changed since this reminder was written. Send a new reminder from the invoice instead.");
        }

        if (!string.Equals(invoice.CustomerEmail, log.Recipient, StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add("The customer's email address has changed. Send a new reminder from the invoice instead.");
        }

        if (reasons.Count > 0)
        {
            return new ReminderSendResult(false, logId, reasons);
        }

        // Claim the failed log atomically so concurrent retries cannot both send.
        var claimed = await _db.ReminderLogs
            .Where(r => r.Id == logId && r.Status == ReminderStatus.Failed)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, ReminderStatus.Queued).SetProperty(r => r.AttemptCount, r => r.AttemptCount + 1), ct);
        if (claimed == 0)
        {
            return new ReminderSendResult(false, logId, new[] { "This reminder is already being retried." });
        }

        return await DeliverAsync(logId, business.Name, business.ContactEmail, ct);
    }

    public async Task<PagedResult<ReminderItem>> HistoryAsync(ReminderStatus? status, int page, CancellationToken ct)
    {
        var query = _db.ReminderLogs.AsNoTracking();
        if (status is { } s)
        {
            query = query.Where(r => r.Status == s);
        }

        var total = await query.CountAsync(ct);
        page = Math.Clamp(page, 1, Math.Max(1, (int)Math.Ceiling(total / (double)HistoryPageSize)));
        var skip = (page - 1) * HistoryPageSize;
        var ids = await query.OrderByDescending(r => r.CreatedAtUtc).ThenByDescending(r => r.Id).Skip(skip).Take(HistoryPageSize).Select(r => r.Id).ToListAsync(ct);
        var items = await _queries.RemindersAsync(r => ids.Contains(r.Id), HistoryPageSize, ct);
        return new PagedResult<ReminderItem>(items, page, HistoryPageSize, total);
    }

    public Task<ReminderLog?> GetLogAsync(int id, CancellationToken ct)
        => _db.ReminderLogs.AsNoTracking().Include(r => r.Customer).Include(r => r.Invoice).SingleOrDefaultAsync(r => r.Id == id, ct);

    private async Task<ReminderSendResult> DeliverAsync(int logId, string fromDisplayName, string? replyTo, CancellationToken ct)
    {
        var log = await _db.ReminderLogs.SingleAsync(r => r.Id == logId, ct);

        EmailSendResult result;
        try
        {
            result = await _sender.SendAsync(new EmailMessage(log.Recipient, log.Subject, log.Body, fromDisplayName, replyTo), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Senders should not throw; if one does, record a generic message rather than raw exception text.
            result = EmailSendResult.Failure("Unexpected email delivery error. See the application log for details.");
        }

        if (result.Succeeded)
        {
            log.Status = ReminderStatus.Sent;
            log.SentAtUtc = _time.GetUtcNow();
            log.ProviderMessageId = result.ProviderMessageId;
            log.LastError = null;
        }
        else
        {
            log.Status = ReminderStatus.Failed;
            var error = result.Error ?? "Delivery failed.";
            log.LastError = error.Length > 1000 ? error[..1000] : error;
        }

        await _db.SaveChangesAsync(CancellationToken.None);
        return new ReminderSendResult(result.Succeeded, log.Id, result.Succeeded ? Array.Empty<string>() : new[] { log.LastError! });
    }

    private async Task<IReadOnlyList<string>> GetBlockingReasonsAsync(Business business, InvoiceSummary invoice, CancellationToken ct)
    {
        var recent = await _db.ReminderLogs.AsNoTracking()
            .Where(r => r.InvoiceId == invoice.Id && r.TemplateKey == ReminderTemplateDefaults.TemplateKey &&
                        (r.Status == ReminderStatus.Sent || r.Status == ReminderStatus.Queued))
            .Select(r => new { r.Status, r.SentAtUtc, r.CreatedAtUtc })
            .ToListAsync(ct);

        var now = _time.GetUtcNow();

        // A Queued log may have been delivered before a crash, so it also counts towards the cooldown.
        var lastSentOrPossiblySent = recent.Max(r => (DateTimeOffset?)(r.SentAtUtc ?? r.CreatedAtUtc));
        var sendInProgress = recent.Any(r => r.Status == ReminderStatus.Queued && r.CreatedAtUtc > now.AddHours(-1));

        var reasons = ReminderEligibility.Evaluate(new ReminderEligibilityInput(
            invoice.CustomerEmail,
            invoice.CustomerIsActive,
            invoice.CustomerRemindersPaused,
            invoice.IsDisputed,
            invoice.RemindersPaused,
            invoice.Financials,
            lastSentOrPossiblySent,
            sendInProgress,
            business.ReminderCooldownDays,
            now)).ToList();

        if (string.IsNullOrWhiteSpace(business.ContactEmail))
        {
            reasons.Add("Add your business contact email in Settings so customers can reply to reminders.");
        }

        return reasons;
    }

    private static string BusinessContact(Business business)
        => business.ContactEmail ?? business.ContactPhone ?? business.Name;
}
