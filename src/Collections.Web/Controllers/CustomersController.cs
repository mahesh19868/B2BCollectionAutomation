using Collections.Web.Models;
using Collections.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Collections.Web.Controllers;

public sealed class CustomersController : AppController
{
    private const int PageSize = 50;
    private readonly CustomerService _customers;

    public CustomersController(CustomerService customers)
    {
        _customers = customers;
    }

    public async Task<IActionResult> Index(string? search, int page = 1, CancellationToken ct = default)
    {
        ViewData["Search"] = search;
        return View(await _customers.ListAsync(search, page, PageSize, ct));
    }

    public async Task<IActionResult> Details(int id, CancellationToken ct)
    {
        var detail = await _customers.GetDetailAsync(id, ct);
        return detail is null ? NotFound() : View(detail);
    }

    [HttpGet]
    public IActionResult Create() => View("Edit", new CustomerFormModel());

    [HttpPost]
    public async Task<IActionResult> Create(CustomerFormModel model, CancellationToken ct)
    {
        var result = await _customers.CreateAsync(model, ct);
        if (!result.Succeeded)
        {
            AddErrors(result);
            return View("Edit", model);
        }

        Flash("Customer created.");
        return RedirectToAction(nameof(Details), new { id = result.Id });
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id, CancellationToken ct)
    {
        var detail = await _customers.GetDetailAsync(id, ct);
        if (detail is null)
        {
            return NotFound();
        }

        var c = detail.Customer;
        return View(new CustomerFormModel
        {
            Id = c.Id, Name = c.Name, CustomerCode = c.CustomerCode, Email = c.Email, Phone = c.Phone,
            IsActive = c.IsActive, RemindersPaused = c.RemindersPaused, Notes = c.Notes,
        });
    }

    [HttpPost]
    public async Task<IActionResult> Edit(int id, CustomerFormModel model, CancellationToken ct)
    {
        var result = await _customers.UpdateAsync(id, model, ct);
        if (result.NotFound)
        {
            return NotFound();
        }

        if (!result.Succeeded)
        {
            model.Id = id;
            AddErrors(result);
            return View(model);
        }

        Flash("Customer updated.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> PauseReminders(int id, bool paused, string? returnUrl, CancellationToken ct)
    {
        var result = await _customers.SetRemindersPausedAsync(id, paused, ct);
        if (result.NotFound)
        {
            return NotFound();
        }

        Flash(paused ? "Reminders paused for this customer." : "Reminders resumed for this customer.");
        return RedirectToLocal(returnUrl, RedirectToAction(nameof(Details), new { id }));
    }
}
