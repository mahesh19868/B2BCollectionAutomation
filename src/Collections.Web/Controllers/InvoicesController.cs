using Collections.Web.Models;
using Collections.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace Collections.Web.Controllers;

public sealed class InvoicesController : AppController
{
    private readonly InvoiceService _invoices;
    private readonly CustomerService _customers;

    public InvoicesController(InvoiceService invoices, CustomerService customers)
    {
        _invoices = invoices;
        _customers = customers;
    }

    public async Task<IActionResult> Index(InvoiceListFilter filter, CancellationToken ct)
    {
        var (page, asOf) = await _invoices.ListAsync(filter, ct);
        return View(new InvoiceListModel { Filter = filter, Page = page, AsOfDate = asOf });
    }

    public async Task<IActionResult> Details(int id, CancellationToken ct)
    {
        var detail = await _invoices.GetDetailAsync(id, ct);
        return detail is null ? NotFound() : View(detail);
    }

    [HttpGet]
    public async Task<IActionResult> Create(int? customerId, CancellationToken ct)
    {
        var model = new InvoiceFormModel { CustomerId = customerId ?? 0 };
        await LoadCustomersAsync(model, ct);
        return View("Edit", model);
    }

    [HttpPost]
    public async Task<IActionResult> Create(InvoiceFormModel model, CancellationToken ct)
    {
        var result = await _invoices.CreateAsync(model, ct);
        if (!result.Succeeded)
        {
            AddErrors(result);
            await LoadCustomersAsync(model, ct);
            return View("Edit", model);
        }

        Flash("Invoice created.");
        return RedirectToAction(nameof(Details), new { id = result.Id });
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id, CancellationToken ct)
    {
        var input = await _invoices.GetForEditAsync(id, ct);
        var detail = await _invoices.GetDetailAsync(id, ct);
        if (input is null || detail is null)
        {
            return NotFound();
        }

        return View(new InvoiceFormModel
        {
            Id = id, CustomerId = input.CustomerId, CustomerName = detail.Summary.CustomerName, InvoiceNumber = input.InvoiceNumber,
            InvoiceDate = input.InvoiceDate, DueDate = input.DueDate, InvoiceAmount = input.InvoiceAmount,
            ExternalReference = input.ExternalReference, IsDisputed = input.IsDisputed, RemindersPaused = input.RemindersPaused,
        });
    }

    [HttpPost]
    public async Task<IActionResult> Edit(int id, InvoiceFormModel model, CancellationToken ct)
    {
        var result = await _invoices.UpdateAsync(id, model, ct);
        if (result.NotFound)
        {
            return NotFound();
        }

        if (!result.Succeeded)
        {
            var current = await _invoices.GetDetailAsync(id, ct);
            model.Id = id;
            model.InvoiceNumber = current?.Summary.InvoiceNumber ?? model.InvoiceNumber;
            model.CustomerName = current?.Summary.CustomerName;
            AddErrors(result);
            return View(model);
        }

        Flash("Invoice updated. The change is recorded in the invoice history.");
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Quick toggles for disputed / reminders-paused from detail and worklist pages.</summary>
    [HttpPost]
    public async Task<IActionResult> Flags(int id, bool? disputed, bool? remindersPaused, string? returnUrl, CancellationToken ct)
    {
        var result = await _invoices.SetFlagsAsync(id, disputed, remindersPaused, ct);
        if (result.NotFound)
        {
            return NotFound();
        }

        Flash("Invoice updated.");
        return RedirectToLocal(returnUrl, RedirectToAction(nameof(Details), new { id }));
    }

    private async Task LoadCustomersAsync(InvoiceFormModel model, CancellationToken ct)
    {
        model.Customers = (await _customers.ListActiveForSelectAsync(ct))
            .Select(c => new SelectListItem(c.Name, c.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), c.Id == model.CustomerId))
            .ToList();
    }
}
