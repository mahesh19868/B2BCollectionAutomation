using System.Data;
using Collections.Core.Entities;
using Collections.Core.Imports;
using Collections.Web.Data;
using Collections.Web.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Collections.Web.Services;

public sealed record ImportUploadResult(int? BatchId, IReadOnlyList<string> FileErrors);

/// <summary>
/// Invoice CSV import: upload → validated preview (stored as rows, the file itself is not kept) →
/// explicit confirmation → single transaction. Never sends reminders and never changes existing customers.
/// </summary>
public sealed class ImportService
{
    public static readonly TimeSpan PreviewLifetime = TimeSpan.FromHours(24);

    private readonly AppDbContext _db;
    private readonly ICurrentBusinessContext _context;
    private readonly TimeProvider _time;
    private readonly ImportLimits _limits;
    private readonly ILogger<ImportService> _logger;

    public ImportService(AppDbContext db, ICurrentBusinessContext context, TimeProvider time, IOptions<ImportLimits> limits, ILogger<ImportService> logger)
    {
        _db = db;
        _context = context;
        _time = time;
        _limits = limits.Value;
        _logger = logger;
    }

    public ImportLimits Limits => _limits;

    public async Task<ImportUploadResult> CreatePreviewAsync(Stream content, string? originalFileName, CancellationToken ct)
    {
        var fileName = FileNameSanitizer.Sanitize(originalFileName);

        // Read at most one byte past the limit so oversized uploads are rejected without buffering them fully.
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > _limits.MaxFileBytes)
            {
                return new ImportUploadResult(null, new[] { $"The file is larger than the {_limits.MaxFileBytes / 1024} KB limit." });
            }
        }

        var file = InvoiceCsvReader.Read(buffer.ToArray(), fileName, _limits);
        if (!file.IsValid)
        {
            return new ImportUploadResult(null, file.FileErrors);
        }

        var plan = await PlanAsync(file.Rows, ct);
        var batch = new ImportBatch
        {
            ImportType = ImportType.Invoices,
            FileName = fileName,
            UploadedByUserId = _context.UserId,
            UploadedAtUtc = _time.GetUtcNow(),
            Status = ImportBatchStatus.Previewed,
            FileMessages = file.Notices.Count == 0 ? null : string.Join("\n", file.Notices),
            TotalRows = plan.Count,
            ValidRows = plan.Count(r => !r.HasErrors),
            RejectedRows = plan.Count(r => r.HasErrors),
            Rows = plan.Select(r => new ImportRow
            {
                RowNumber = r.Raw.RowNumber,
                CustomerCode = Limit(r.Raw.CustomerCode),
                CustomerName = Limit(r.Raw.CustomerName),
                CustomerEmail = Limit(r.Raw.CustomerEmail),
                InvoiceNumber = Limit(r.Raw.InvoiceNumber),
                InvoiceDate = Limit(r.Raw.InvoiceDate),
                DueDate = Limit(r.Raw.DueDate),
                InvoiceAmount = Limit(r.Raw.InvoiceAmount),
                PlannedAction = r.Action,
                Errors = Join(r.Errors.Select(e => e.ToString())),
                Notes = Join(r.Notes),
            }).ToList(),
        };

        _db.ImportBatches.Add(batch);
        await _db.SaveChangesAsync(ct);
        return new ImportUploadResult(batch.Id, Array.Empty<string>());
    }

    public Task<ImportBatch?> GetBatchAsync(int id, CancellationToken ct)
        => _db.ImportBatches.AsNoTracking()
            .Include(b => b.Rows.OrderBy(r => r.RowNumber))
            .SingleOrDefaultAsync(b => b.Id == id, ct);

    public Task<List<ImportBatch>> RecentAsync(int take, CancellationToken ct)
        => _db.ImportBatches.AsNoTracking().OrderByDescending(b => b.UploadedAtUtc).ThenByDescending(b => b.Id).Take(take).ToListAsync(ct);

    public async Task<ServiceResult> CancelAsync(int id, CancellationToken ct)
    {
        var updated = await _db.ImportBatches
            .Where(b => b.Id == id && b.Status == ImportBatchStatus.Previewed)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, ImportBatchStatus.Cancelled).SetProperty(b => b.CompletedAtUtc, _time.GetUtcNow()), ct);
        return updated == 1 ? ServiceResult.Ok(id) : ServiceResult.Fail(string.Empty, "This import can no longer be cancelled.");
    }

    /// <summary>
    /// Commits a previewed import. Rows are re-validated against current data inside one transaction;
    /// either all accepted rows are saved and every row gets a recorded outcome, or nothing is saved and the batch is marked failed.
    /// </summary>
    public async Task<ServiceResult> CommitAsync(int id, bool approveUpdates, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var oldest = now - PreviewLifetime;

        // Claim the batch so a double-submitted confirmation cannot run twice.
        var claimed = await _db.ImportBatches
            .Where(b => b.Id == id && b.Status == ImportBatchStatus.Previewed && b.UploadedAtUtc >= oldest)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, ImportBatchStatus.Committing), ct);
        if (claimed == 0)
        {
            var exists = await _db.ImportBatches.AnyAsync(b => b.Id == id, ct);
            return exists
                ? ServiceResult.Fail(string.Empty, "This import was already confirmed, cancelled, or the preview expired (24 hours). Upload the file again.")
                : ServiceResult.Missing();
        }

        try
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);

            var batch = await _db.ImportBatches.Include(b => b.Rows).SingleAsync(b => b.Id == id, ct);
            var rawRows = batch.Rows.OrderBy(r => r.RowNumber)
                .Select(r => new InvoiceImportRawRow(r.RowNumber, r.CustomerCode, r.CustomerName, r.CustomerEmail, r.InvoiceNumber, r.InvoiceDate, r.DueDate, r.InvoiceAmount))
                .ToList();

            var plan = await PlanAsync(rawRows, ct);
            var rowsByNumber = batch.Rows.ToDictionary(r => r.RowNumber);
            var newCustomers = new Dictionary<string, Customer>();
            var userId = _context.UserId;

            foreach (var planned in plan)
            {
                var row = rowsByNumber[planned.Raw.RowNumber];
                row.Errors = Join(planned.Errors.Select(e => e.ToString()));
                row.Notes = Join(planned.Notes);

                switch (planned.Action)
                {
                    case ImportRowAction.Create:
                    {
                        var invoice = new Invoice
                        {
                            InvoiceNumber = planned.InvoiceNumber,
                            InvoiceDate = planned.InvoiceDate,
                            DueDate = planned.DueDate,
                            InvoiceAmount = planned.InvoiceAmount,
                        };

                        if (planned.ExistingCustomerId is { } customerId)
                        {
                            invoice.CustomerId = customerId;
                        }
                        else
                        {
                            var key = planned.NewCustomerKey!;
                            if (!newCustomers.TryGetValue(key, out var customer))
                            {
                                customer = new Customer
                                {
                                    Name = planned.CustomerName,
                                    CustomerCode = planned.CustomerCode,
                                    Email = InvoiceImportPlanner.EmailForNewCustomer(plan, key),
                                };
                                newCustomers[key] = customer;
                                _db.Customers.Add(customer);
                            }

                            invoice.Customer = customer;
                        }

                        _db.Invoices.Add(invoice);
                        row.Outcome = ImportRowOutcome.Created;
                        batch.CreatedCount++;
                        break;
                    }
                    case ImportRowAction.Update when approveUpdates:
                    {
                        var invoice = await _db.Invoices.SingleAsync(i => i.Id == planned.ExistingInvoiceId, ct);
                        invoice.InvoiceDate = planned.InvoiceDate;
                        invoice.DueDate = planned.DueDate;
                        invoice.InvoiceAmount = planned.InvoiceAmount;
                        _db.AddSystemNote(invoice.CustomerId, invoice.Id,
                            $"Invoice {invoice.InvoiceNumber} updated by import #{batch.Id} ({batch.FileName}, row {row.RowNumber}). {planned.Notes.LastOrDefault()}",
                            userId, now);
                        row.Outcome = ImportRowOutcome.Updated;
                        batch.UpdatedCount++;
                        break;
                    }
                    case ImportRowAction.Update:
                        row.Outcome = ImportRowOutcome.SkippedUpdateNotApproved;
                        batch.SkippedCount++;
                        break;
                    case ImportRowAction.Unchanged:
                        row.Outcome = ImportRowOutcome.SkippedUnchanged;
                        batch.SkippedCount++;
                        break;
                    default:
                        row.Outcome = ImportRowOutcome.Rejected;
                        break;
                }
            }

            batch.ValidRows = plan.Count(r => !r.HasErrors);
            batch.RejectedRows = plan.Count(r => r.HasErrors);
            batch.UpdatesApproved = approveUpdates;
            batch.Status = ImportBatchStatus.Committed;
            batch.CompletedAtUtc = _time.GetUtcNow();

            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return ServiceResult.Ok(id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Import batch {BatchId} failed and was rolled back", id);
            _db.ChangeTracker.Clear();
            await _db.ImportBatches.Where(b => b.Id == id).ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, ImportBatchStatus.Failed)
                .SetProperty(b => b.CompletedAtUtc, _time.GetUtcNow())
                .SetProperty(b => b.FailureMessage, "The import could not be saved and nothing was imported. The data may have changed while you were reviewing; upload the file again."), CancellationToken.None);
            return ServiceResult.Fail(string.Empty, "The import failed and no rows were saved. Upload the file again to retry.");
        }
    }

    public async Task<string?> BuildErrorReportAsync(int id, CancellationToken ct)
    {
        var batch = await GetBatchAsync(id, ct);
        return batch is null ? null : ImportErrorReport.Build(batch.Rows);
    }

    private async Task<IReadOnlyList<PlannedInvoiceRow>> PlanAsync(IReadOnlyList<InvoiceImportRawRow> rows, CancellationToken ct)
    {
        var customers = await _db.Customers.AsNoTracking()
            .Select(c => new ExistingCustomer(c.Id, c.CustomerCode, c.Name, c.Email))
            .ToListAsync(ct);

        var numbers = rows.Where(r => !string.IsNullOrWhiteSpace(r.InvoiceNumber)).Select(r => r.InvoiceNumber!.Trim()).Distinct().ToList();
        var invoices = await _db.Invoices.AsNoTracking()
            .Where(i => numbers.Contains(i.InvoiceNumber))
            .Select(i => new ExistingInvoice(i.Id, i.InvoiceNumber, i.CustomerId, i.Customer!.Name, i.InvoiceDate, i.DueDate, i.InvoiceAmount,
                i.Allocations.Sum(a => (decimal?)a.AllocatedAmount) ?? 0m))
            .ToListAsync(ct);

        return InvoiceImportPlanner.Plan(rows, customers, invoices);
    }

    private static string? Join(IEnumerable<string> lines)
    {
        var text = string.Join("\n", lines);
        return text.Length == 0 ? null : text;
    }

    private static string? Limit(string? value) => value is { Length: > 500 } ? value[..500] : value;
}
