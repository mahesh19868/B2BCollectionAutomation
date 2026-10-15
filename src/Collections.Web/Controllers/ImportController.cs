using System.Text;
using Collections.Core.Entities;
using Collections.Core.Imports;
using Collections.Web.Infrastructure;
using Collections.Web.Models;
using Collections.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Collections.Web.Controllers;

public sealed class ImportController : AppController
{
    private const long UploadRequestLimit = 10 * 1024 * 1024;

    private readonly ImportService _imports;
    private readonly BusinessService _business;
    private readonly TimeProvider _time;

    public ImportController(ImportService imports, BusinessService business, TimeProvider time)
    {
        _imports = imports;
        _business = business;
        _time = time;
    }

    public async Task<IActionResult> Index(CancellationToken ct) => View(await BuildIndexAsync(Array.Empty<string>(), ct));

    public IActionResult Template()
        => File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(InvoiceCsvReader.BuildTemplate())).ToArray(),
            "text/csv", "invoice-import-template.csv");

    [HttpPost, RequestSizeLimit(UploadRequestLimit), EnableRateLimiting(RateLimitPolicies.Expensive)]
    public async Task<IActionResult> Upload(IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            return View(nameof(Index), await BuildIndexAsync(new[] { "Choose a CSV file to upload." }, ct));
        }

        await using var stream = file.OpenReadStream();
        var result = await _imports.CreatePreviewAsync(stream, file.FileName, ct);
        if (result.BatchId is null)
        {
            return View(nameof(Index), await BuildIndexAsync(result.FileErrors, ct));
        }

        return RedirectToAction(nameof(Batch), new { id = result.BatchId });
    }

    /// <summary>Preview (before confirmation) and results (after) for one import.</summary>
    public async Task<IActionResult> Batch(int id, CancellationToken ct)
    {
        var batch = await _imports.GetBatchAsync(id, ct);
        if (batch is null)
        {
            return NotFound();
        }

        var business = await _business.GetCurrentAsync(ct);
        return View(new ImportBatchModel
        {
            Batch = batch,
            TimeZoneId = business.TimeZoneId,
            IsExpired = batch.Status == ImportBatchStatus.Previewed && batch.UploadedAtUtc < _time.GetUtcNow() - ImportService.PreviewLifetime,
        });
    }

    [HttpPost, EnableRateLimiting(RateLimitPolicies.Expensive)]
    public async Task<IActionResult> Confirm(int id, bool approveUpdates, CancellationToken ct)
    {
        var result = await _imports.CommitAsync(id, approveUpdates, ct);
        if (result.NotFound)
        {
            return NotFound();
        }

        if (result.Succeeded)
        {
            Flash("Import completed. Review the outcome of each row below.");
        }
        else
        {
            FlashError(result.Errors[0].Message);
        }

        return RedirectToAction(nameof(Batch), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> Cancel(int id, CancellationToken ct)
    {
        var result = await _imports.CancelAsync(id, ct);
        if (result.Succeeded)
        {
            Flash("Import cancelled. Nothing was imported.");
        }
        else
        {
            FlashError(result.Errors[0].Message);
        }

        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Errors(int id, CancellationToken ct)
    {
        var report = await _imports.BuildErrorReportAsync(id, ct);
        return report is null
            ? NotFound()
            : File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(report)).ToArray(), "text/csv", $"import-{id}-errors.csv");
    }

    private async Task<ImportIndexModel> BuildIndexAsync(IReadOnlyList<string> errors, CancellationToken ct)
    {
        var business = await _business.GetCurrentAsync(ct);
        return new ImportIndexModel
        {
            FileErrors = errors,
            Recent = await _imports.RecentAsync(20, ct),
            Limits = _imports.Limits,
            TimeZoneId = business.TimeZoneId,
        };
    }
}
