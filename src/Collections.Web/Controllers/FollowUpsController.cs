using Collections.Core.Entities;
using Collections.Web.Models;
using Collections.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Collections.Web.Controllers;

/// <summary>Follow-up notes and promises to pay.</summary>
public sealed class FollowUpsController : AppController
{
    private readonly FollowUpService _followUps;

    public FollowUpsController(FollowUpService followUps)
    {
        _followUps = followUps;
    }

    [HttpGet]
    public async Task<IActionResult> Create(int customerId, int? invoiceId, string? returnUrl, CancellationToken ct)
    {
        var target = await _followUps.GetTargetAsync(customerId, invoiceId, ct);
        if (target is null)
        {
            return NotFound();
        }

        return View(new FollowUpFormModel
        {
            CustomerId = customerId, InvoiceId = invoiceId, ReturnUrl = returnUrl,
            CustomerName = target.Value.CustomerName, InvoiceNumber = target.Value.InvoiceNumber,
        });
    }

    [HttpPost]
    public async Task<IActionResult> Create(FollowUpFormModel model, CancellationToken ct)
    {
        var result = await _followUps.AddActivityAsync(model, ct);
        if (result.NotFound)
        {
            return NotFound();
        }

        if (!result.Succeeded)
        {
            AddErrors(result);
            var target = await _followUps.GetTargetAsync(model.CustomerId, model.InvoiceId, ct);
            model.CustomerName = target?.CustomerName;
            model.InvoiceNumber = target?.InvoiceNumber;
            return View(model);
        }

        Flash("Follow-up recorded.");
        return RedirectToLocal(model.ReturnUrl, Back(model.CustomerId, model.InvoiceId));
    }

    private IActionResult Back(int customerId, int? invoiceId)
        => invoiceId is { } id ? RedirectToAction("Details", "Invoices", new { id }) : RedirectToAction("Details", "Customers", new { id = customerId });
}

public sealed class PromisesController : AppController
{
    private readonly FollowUpService _followUps;

    public PromisesController(FollowUpService followUps)
    {
        _followUps = followUps;
    }

    public async Task<IActionResult> Index(PromiseListView view = PromiseListView.Open, CancellationToken ct = default)
    {
        var (items, asOf) = await _followUps.ListPromisesAsync(view, ct);
        ViewData["View"] = view;
        ViewData["AsOf"] = asOf;
        return View(items);
    }

    [HttpGet]
    public async Task<IActionResult> Create(int customerId, int? invoiceId, string? returnUrl, CancellationToken ct)
    {
        var target = await _followUps.GetTargetAsync(customerId, invoiceId, ct);
        if (target is null)
        {
            return NotFound();
        }

        return View(new PromiseFormModel
        {
            CustomerId = customerId, InvoiceId = invoiceId, ReturnUrl = returnUrl,
            CustomerName = target.Value.CustomerName, InvoiceNumber = target.Value.InvoiceNumber,
        });
    }

    [HttpPost]
    public async Task<IActionResult> Create(PromiseFormModel model, CancellationToken ct)
    {
        var result = await _followUps.AddPromiseAsync(model, ct);
        if (result.NotFound)
        {
            return NotFound();
        }

        if (!result.Succeeded)
        {
            AddErrors(result);
            var target = await _followUps.GetTargetAsync(model.CustomerId, model.InvoiceId, ct);
            model.CustomerName = target?.CustomerName;
            model.InvoiceNumber = target?.InvoiceNumber;
            return View(model);
        }

        Flash("Promise to pay recorded. It does not change the invoice balance.");
        return RedirectToLocal(model.ReturnUrl, model.InvoiceId is { } id
            ? RedirectToAction("Details", "Invoices", new { id })
            : RedirectToAction("Details", "Customers", new { id = model.CustomerId }));
    }

    [HttpGet]
    public async Task<IActionResult> Resolve(int id, string? returnUrl, CancellationToken ct)
    {
        var promise = await _followUps.GetPromiseAsync(id, ct);
        return promise is null ? NotFound() : View(new ResolvePromiseModel { Id = id, Promise = promise, ReturnUrl = returnUrl });
    }

    [HttpPost]
    public async Task<IActionResult> Resolve(ResolvePromiseModel model, CancellationToken ct)
    {
        var result = await _followUps.ResolvePromiseAsync(model.Id, model.Outcome, model.ResolutionNotes, ct);
        if (result.NotFound)
        {
            return NotFound();
        }

        if (!result.Succeeded)
        {
            AddErrors(result);
            model.Promise = await _followUps.GetPromiseAsync(model.Id, ct);
            return View(model);
        }

        Flash($"Promise marked {model.Outcome}.");
        return RedirectToLocal(model.ReturnUrl, RedirectToAction("Details", "Customers", new { id = result.Id }));
    }
}
