using Collections.Web.Models;
using Collections.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace Collections.Web.Controllers;

public sealed class PaymentsController : AppController
{
    private readonly PaymentService _payments;
    private readonly CustomerService _customers;
    private readonly BusinessService _business;

    public PaymentsController(PaymentService payments, CustomerService customers, BusinessService business)
    {
        _payments = payments;
        _customers = customers;
        _business = business;
    }

    public async Task<IActionResult> Index(string? search, int page = 1, CancellationToken ct = default)
    {
        ViewData["Search"] = search;
        return View(await _payments.ListAsync(search, page, ct));
    }

    public async Task<IActionResult> Details(int id, CancellationToken ct)
    {
        var detail = await _payments.GetDetailAsync(id, ct);
        return detail is null ? NotFound() : View(detail);
    }

    /// <summary>Step 1: choose a customer. Step 2 (customerId set): enter the payment and allocate it.</summary>
    [HttpGet]
    public async Task<IActionResult> Create(int? customerId, CancellationToken ct)
    {
        var model = new PaymentFormModel { CustomerId = customerId ?? 0, PaymentDate = await _business.GetTodayAsync(ct) };
        return await ShowFormAsync(model, ct);
    }

    [HttpPost]
    public async Task<IActionResult> Create(PaymentFormModel model, CancellationToken ct)
    {
        var result = await _payments.RecordAsync(model, ct);
        if (result.NotFound)
        {
            return NotFound();
        }

        if (!result.Succeeded)
        {
            AddErrors(result);
            return await ShowFormAsync(model, ct);
        }

        Flash("Payment recorded.");
        return RedirectToAction(nameof(Details), new { id = result.Id });
    }

    [HttpGet]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var detail = await _payments.GetDetailAsync(id, ct);
        return detail is null ? NotFound() : View(detail);
    }

    [HttpPost, ActionName("Delete")]
    public async Task<IActionResult> DeleteConfirmed(int id, CancellationToken ct)
    {
        var result = await _payments.DeleteAsync(id, ct);
        if (result.NotFound)
        {
            return NotFound();
        }

        Flash("Payment deleted. Invoice balances were restored and the deletion is recorded in the customer history.");
        return RedirectToAction("Details", "Customers", new { id = result.Id });
    }

    private async Task<IActionResult> ShowFormAsync(PaymentFormModel model, CancellationToken ct)
    {
        if (model.CustomerId > 0)
        {
            var target = await _customers.GetDetailAsync(model.CustomerId, ct);
            if (target is null)
            {
                return NotFound();
            }

            model.CustomerName = target.Customer.Name;
            model.OpenInvoices = await _payments.GetOpenInvoicesAsync(model.CustomerId, ct);

            // Keep any amounts the user already typed when redisplaying after validation errors.
            var typed = model.Allocations.Where(a => a.Amount is not null)
                .GroupBy(a => a.InvoiceId).ToDictionary(g => g.Key, g => g.First().Amount);
            model.Allocations = model.OpenInvoices.Select(i => new AllocationInput { InvoiceId = i.InvoiceId, Amount = typed.GetValueOrDefault(i.InvoiceId) }).ToList();
        }
        else
        {
            model.CustomerOptions = (await _customers.ListActiveForSelectAsync(ct))
                .Select(c => new SelectListItem(c.Name, c.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                .ToList();
        }

        return View("Create", model);
    }
}
