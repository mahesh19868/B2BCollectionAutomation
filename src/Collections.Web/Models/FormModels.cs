using Collections.Core.Entities;
using Collections.Web.Services;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace Collections.Web.Models;

public sealed class CustomerFormModel : CustomerInput
{
    public int? Id { get; set; }
}

public sealed class InvoiceFormModel : InvoiceInput
{
    public int? Id { get; set; }
    public string? CustomerName { get; set; }
    public List<SelectListItem> Customers { get; set; } = new();
}

public sealed class PaymentFormModel : PaymentInput
{
    public string? CustomerName { get; set; }
    public List<SelectListItem> CustomerOptions { get; set; } = new();
    public IReadOnlyList<OpenInvoiceOption> OpenInvoices { get; set; } = Array.Empty<OpenInvoiceOption>();
}

public sealed class FollowUpFormModel : FollowUpInput
{
    public string? CustomerName { get; set; }
    public string? InvoiceNumber { get; set; }
    public string? ReturnUrl { get; set; }
}

public sealed class PromiseFormModel : PromiseInput
{
    public string? CustomerName { get; set; }
    public string? InvoiceNumber { get; set; }
    public string? ReturnUrl { get; set; }
}

public sealed class ResolvePromiseModel
{
    public int Id { get; set; }
    public PromiseStatus Outcome { get; set; } = PromiseStatus.Kept;
    public string? ResolutionNotes { get; set; }
    public string? ReturnUrl { get; set; }
    public PromiseItem? Promise { get; set; }
}

public sealed class SettingsFormModel : BusinessSettingsInput
{
    public string TimeZoneId { get; set; } = Business.DefaultTimeZoneId;
}

public sealed class InvoiceListModel
{
    public required InvoiceListFilter Filter { get; init; }
    public required PagedResult<InvoiceSummary> Page { get; init; }
    public required DateOnly AsOfDate { get; init; }
}

public sealed class WorklistModel
{
    public required WorklistFilter Filter { get; init; }
    public required WorklistResult Result { get; init; }
    public required string CurrentUrl { get; init; }
}

public sealed class ImportIndexModel
{
    public IReadOnlyList<string> FileErrors { get; init; } = Array.Empty<string>();
    public required IReadOnlyList<ImportBatch> Recent { get; init; }
    public required Collections.Core.Imports.ImportLimits Limits { get; init; }
    public required string TimeZoneId { get; init; }
}

public sealed class ImportBatchModel
{
    public required ImportBatch Batch { get; init; }
    public required string TimeZoneId { get; init; }
    public required bool IsExpired { get; init; }
}
