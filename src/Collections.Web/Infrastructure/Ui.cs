using Collections.Core.Entities;
using Collections.Core.Financial;
using Collections.Core.Formatting;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.WebUtilities;

namespace Collections.Web.Infrastructure;

/// <summary>Display helpers for views. Status badges always include text so meaning never relies on colour.</summary>
public static class Ui
{
    public static string Money(decimal amount) => MoneyFormat.Inr(amount);

    public static string Date(DateOnly date) => DateFormat.Display(date);

    public static string Date(DateOnly? date) => DateFormat.Display(date);

    public static string DateTime(DateTimeOffset? utc, string timeZoneId) => utc is null ? "—" : DateFormat.Display(utc.Value, timeZoneId);

    public static string InputDate(DateOnly? date) => date?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

    public static string InputAmount(decimal? amount) => amount is null ? string.Empty : MoneyFormat.Plain(amount.Value);

    public static (string Text, string Css) Badge(InvoiceStatus status, bool partiallyPaid = false) => status switch
    {
        InvoiceStatus.Overdue => ("Overdue", "text-bg-danger"),
        InvoiceStatus.DueToday => ("Due today", "text-bg-warning"),
        InvoiceStatus.NotDue => (partiallyPaid ? "Not due · part paid" : "Not due", "text-bg-secondary"),
        InvoiceStatus.Paid => ("Paid", "text-bg-success"),
        InvoiceStatus.InvalidData => ("Data error", "text-bg-dark"),
        _ => (status.ToString(), "text-bg-light"),
    };

    public static (string Text, string Css) Badge(PromiseDisplayState state) => state switch
    {
        PromiseDisplayState.PastDueUnresolved => ("Date passed – unresolved", "text-bg-danger"),
        PromiseDisplayState.DueToday => ("Due today", "text-bg-warning"),
        PromiseDisplayState.Upcoming => ("Upcoming", "text-bg-info"),
        PromiseDisplayState.Kept => ("Kept", "text-bg-success"),
        PromiseDisplayState.Missed => ("Missed", "text-bg-dark"),
        PromiseDisplayState.Cancelled => ("Cancelled", "text-bg-light border"),
        _ => (state.ToString(), "text-bg-light"),
    };

    public static (string Text, string Css) Badge(ReminderStatus status) => status switch
    {
        ReminderStatus.Sent => ("Sent", "text-bg-success"),
        ReminderStatus.Failed => ("Failed", "text-bg-danger"),
        ReminderStatus.Queued => ("Sending", "text-bg-warning"),
        ReminderStatus.Cancelled => ("Cancelled", "text-bg-light border"),
        _ => (status.ToString(), "text-bg-light"),
    };

    public static string Label(ActivityType type) => type switch
    {
        ActivityType.System => "System",
        _ => type.ToString(),
    };

    public static string Label(AgeingBucket bucket) => InvoiceCalculator.Describe(bucket);

    public static string Label(ImportRowAction action) => action switch
    {
        ImportRowAction.Create => "Will create",
        ImportRowAction.Update => "Will update (if approved)",
        ImportRowAction.Unchanged => "Unchanged – skip",
        ImportRowAction.Error => "Rejected",
        _ => action.ToString(),
    };

    public static string Label(ImportRowOutcome? outcome) => outcome switch
    {
        ImportRowOutcome.Created => "Created",
        ImportRowOutcome.Updated => "Updated",
        ImportRowOutcome.SkippedUnchanged => "Skipped (unchanged)",
        ImportRowOutcome.SkippedUpdateNotApproved => "Skipped (update not approved)",
        ImportRowOutcome.Rejected => "Rejected",
        _ => "—",
    };

    public static string[] Lines(string? text) => string.IsNullOrEmpty(text) ? Array.Empty<string>() : text.Split('\n');

    /// <summary>Renders a badge; text is HTML-encoded by TagBuilder.</summary>
    public static IHtmlContent Tag((string Text, string Css) badge)
    {
        var tag = new TagBuilder("span");
        tag.AddCssClass("badge");
        tag.AddCssClass(badge.Css);
        tag.InnerHtml.Append(badge.Text);
        return tag;
    }

    /// <summary>Current URL with the page parameter replaced; keeps all filters.</summary>
    public static string PageUrl(HttpRequest request, int page)
    {
        var items = QueryHelpers.ParseQuery(request.QueryString.Value)
            .Where(kv => !string.Equals(kv.Key, "page", StringComparison.OrdinalIgnoreCase))
            .SelectMany(kv => kv.Value.Select(v => new KeyValuePair<string, string?>(kv.Key, v)))
            .Append(new KeyValuePair<string, string?>("page", page.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return request.PathBase + request.Path + QueryString.Create(items);
    }
}
