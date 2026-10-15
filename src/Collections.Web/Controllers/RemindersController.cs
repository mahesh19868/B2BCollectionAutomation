using Collections.Core.Entities;
using Collections.Web.Infrastructure;
using Collections.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Collections.Web.Controllers;

public sealed class RemindersController : AppController
{
    private readonly ReminderService _reminders;
    private readonly BusinessService _business;

    public RemindersController(ReminderService reminders, BusinessService business)
    {
        _reminders = reminders;
        _business = business;
    }

    public async Task<IActionResult> Index(ReminderStatus? status, int page = 1, CancellationToken ct = default)
    {
        ViewData["Status"] = status;
        ViewData["TimeZoneId"] = (await _business.GetCurrentAsync(ct)).TimeZoneId;
        return View(await _reminders.HistoryAsync(status, page, ct));
    }

    public async Task<IActionResult> Details(int id, CancellationToken ct)
    {
        var log = await _reminders.GetLogAsync(id, ct);
        if (log is null)
        {
            return NotFound();
        }

        ViewData["TimeZoneId"] = (await _business.GetCurrentAsync(ct)).TimeZoneId;
        return View(log);
    }

    /// <summary>Shows exactly what will be sent and why it may be blocked. Nothing is sent from here.</summary>
    [HttpGet]
    public async Task<IActionResult> Preview(int invoiceId, CancellationToken ct)
    {
        var preview = await _reminders.PreviewAsync(invoiceId, ct);
        return preview is null ? NotFound() : View(preview);
    }

    [HttpPost, EnableRateLimiting(RateLimitPolicies.Expensive)]
    public async Task<IActionResult> Send(int invoiceId, string idempotencyKey, CancellationToken ct)
    {
        var result = await _reminders.SendAsync(invoiceId, idempotencyKey, ct);
        if (result.NotFound)
        {
            return NotFound();
        }

        if (result.LogId is { } logId)
        {
            if (result.Sent)
            {
                Flash("Reminder sent and recorded in the reminder log.");
            }
            else if (result.Errors.Count > 0)
            {
                FlashError("Reminder delivery failed: " + result.Errors[0] + " You can retry it from this page.");
            }

            return RedirectToAction(nameof(Details), new { id = logId });
        }

        FlashError("Reminder not sent: " + string.Join(" ", result.Errors));
        return RedirectToAction(nameof(Preview), new { invoiceId });
    }

    [HttpPost, EnableRateLimiting(RateLimitPolicies.Expensive)]
    public async Task<IActionResult> Retry(int id, CancellationToken ct)
    {
        var result = await _reminders.RetryAsync(id, ct);
        if (result.NotFound)
        {
            return NotFound();
        }

        if (result.Sent)
        {
            Flash("Reminder sent on retry.");
        }
        else
        {
            FlashError("Retry not completed: " + string.Join(" ", result.Errors));
        }

        return RedirectToAction(nameof(Details), new { id });
    }
}
