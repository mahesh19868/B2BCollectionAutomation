using Collections.Core.Reminders;
using Collections.Web.Models;
using Collections.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Collections.Web.Controllers;

public sealed class SettingsController : AppController
{
    private readonly BusinessService _business;

    public SettingsController(BusinessService business)
    {
        _business = business;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var b = await _business.GetCurrentAsync(ct);
        return View(new SettingsFormModel
        {
            Name = b.Name, ContactEmail = b.ContactEmail, ContactPhone = b.ContactPhone, TimeZoneId = b.TimeZoneId,
            ReminderCooldownDays = b.ReminderCooldownDays, ReminderSubjectTemplate = b.ReminderSubjectTemplate,
            ReminderBodyTemplate = b.ReminderBodyTemplate,
        });
    }

    [HttpPost]
    public async Task<IActionResult> Index(SettingsFormModel model, CancellationToken ct)
    {
        var result = await _business.UpdateSettingsAsync(model, ct);
        if (!result.Succeeded)
        {
            AddErrors(result);
            model.TimeZoneId = (await _business.GetCurrentAsync(ct)).TimeZoneId;
            return View(model);
        }

        Flash("Settings saved.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> ResetTemplate(CancellationToken ct)
    {
        var b = await _business.GetCurrentAsync(ct);
        var model = new SettingsFormModel
        {
            Name = b.Name, ContactEmail = b.ContactEmail, ContactPhone = b.ContactPhone, TimeZoneId = b.TimeZoneId,
            ReminderCooldownDays = b.ReminderCooldownDays,
            ReminderSubjectTemplate = ReminderTemplateDefaults.Subject,
            ReminderBodyTemplate = ReminderTemplateDefaults.Body,
        };
        ModelState.Clear();
        Flash("Default template loaded. Review it and click Save to apply.");
        return View(nameof(Index), model);
    }
}
