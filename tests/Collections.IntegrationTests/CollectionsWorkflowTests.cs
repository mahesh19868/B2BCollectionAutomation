using System.Net;
using Collections.Core.Entities;
using Collections.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Collections.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public sealed class CollectionsWorkflowTests
{
    private readonly AppFactory _factory;

    public CollectionsWorkflowTests(AppFactory factory)
    {
        _factory = factory;
        _factory.Time.UtcNow = AppFactory.FixedNow;
    }

    [Fact]
    public async Task Dashboard_shows_hand_calculated_totals()
    {
        var user = await Browser.RegisterAsync(_factory);
        var today = AppFactory.Today;
        var c1 = await user.CreateCustomerAsync("Dash One");
        var c2 = await user.CreateCustomerAsync("Dash Two");

        var overdue10 = await user.CreateInvoiceAsync(c1, "D-1", 10_000m, today.AddDays(-10));
        var overdue45 = await user.CreateInvoiceAsync(c1, "D-2", 20_000m, today.AddDays(-45));
        await user.CreateInvoiceAsync(c2, "D-3", 30_000m, today.AddDays(-61));
        var paid = await user.CreateInvoiceAsync(c2, "D-4", 5_000m, today.AddDays(-100));
        await user.CreateInvoiceAsync(c2, "D-5", 4_000m, today);
        await user.CreateInvoiceAsync(c2, "D-6", 6_000m, today.AddDays(7));
        await user.CreateInvoiceAsync(c2, "D-7", 8_000m, today.AddDays(8));

        Arrange.AssertRedirect(await user.RecordPaymentAsync(c1, 5_000m, today, (overdue45, 5_000m)));
        Arrange.AssertRedirect(await user.RecordPaymentAsync(c2, 5_000m, today.AddDays(-1), (paid, 5_000m)));
        Arrange.AssertRedirect(await user.RecordPaymentAsync(c1, 700m, new DateOnly(2026, 9, 30))); // previous month, unallocated
        Arrange.AssertRedirect(await user.RecordPromiseAsync(c1, overdue10, 1_000m, today));

        await using (var db = _factory.CreateDbContext(user.BusinessId))
        {
            // An open promise whose date has passed (recorded earlier).
            db.PromisesToPay.Add(new PromiseToPay { CustomerId = c1, PromisedAmount = 500m, PromisedDate = today.AddDays(-2), CreatedAtUtc = AppFactory.FixedNow });
            await db.SaveChangesAsync();
        }

        var html = await user.GetHtmlAsync("/");

        // 10,000 + (20,000 − 5,000) + 30,000 + 4,000 + 6,000 + 8,000. D-4 is paid; the 700 unallocated payment reduces nothing.
        AssertCard(html, "Total outstanding", "₹73,000.00");
        AssertCard(html, "Overdue (due date before today)", "₹55,000.00");
        AssertCard(html, "Due in next 7 days (incl. today)", "₹10,000.00"); // 4,000 due today + 6,000 due in exactly 7 days
        Assert.Contains("Due today: ₹4,000.00", html);
        AssertCard(html, "Collections this month", "₹10,000.00");           // 5,000 + 5,000; the 700 is dated September
        Assert.Contains("1 open promise(s) past their date", html.Replace("<strong>", "").Replace("</strong>", ""));
    }

    private static void AssertCard(string html, string label, string amount)
    {
        var pattern = System.Text.RegularExpressions.Regex.Escape(label) + @"</div>\s*<div class=""fs-4 fw-semibold"">" + System.Text.RegularExpressions.Regex.Escape(amount) + "<";
        Assert.Matches(pattern, html);
    }

    [Fact]
    public async Task Payment_allocation_reduces_outstanding_and_cannot_exceed_balance()
    {
        var user = await Browser.RegisterAsync(_factory);
        var customer = await user.CreateCustomerAsync("Alloc Co");
        var invoice = await user.CreateInvoiceAsync(customer, "AL-1", 1_000m, AppFactory.Today.AddDays(-5));

        var tooMuch = await user.RecordPaymentAsync(customer, 2_000m, AppFactory.Today, (invoice, 1_500m));
        Assert.Equal(HttpStatusCode.OK, tooMuch.StatusCode);
        Assert.Contains("exceeds the outstanding balance", WebUtility.HtmlDecode(await tooMuch.Content.ReadAsStringAsync()));

        var overPayment = await user.RecordPaymentAsync(customer, 100m, AppFactory.Today, (invoice, 200m));
        Assert.Contains("exceeds the payment amount", WebUtility.HtmlDecode(await overPayment.Content.ReadAsStringAsync()));

        var future = await user.RecordPaymentAsync(customer, 100m, AppFactory.Today.AddDays(1), (invoice, 100m));
        Assert.Contains("cannot be in the future", WebUtility.HtmlDecode(await future.Content.ReadAsStringAsync()));

        Arrange.AssertRedirect(await user.RecordPaymentAsync(customer, 600m, AppFactory.Today, (invoice, 400m)));
        var detail = await user.GetHtmlAsync($"/Invoices/Details/{invoice}");
        Assert.Contains("₹600.00", detail);         // outstanding
        Assert.Contains("Partially paid", detail);

        var customerPage = await user.GetHtmlAsync($"/Customers/Details/{customer}");
        Assert.Contains("₹200.00", customerPage);   // unallocated remainder kept visible

        await using var db = _factory.CreateDbContext(user.BusinessId);
        Assert.Equal(400m, await db.PaymentAllocations.SumAsync(a => a.AllocatedAmount));
        Assert.Equal(1, await db.Payments.CountAsync());
    }

    [Fact]
    public async Task Promise_does_not_reduce_outstanding_and_is_resolved_only_by_user_once()
    {
        var user = await Browser.RegisterAsync(_factory);
        var customer = await user.CreateCustomerAsync("Promise Co");
        var invoice = await user.CreateInvoiceAsync(customer, "PR-1", 3_000m, AppFactory.Today.AddDays(-20));

        Arrange.AssertRedirect(await user.RecordPromiseAsync(customer, invoice, 3_000m, AppFactory.Today.AddDays(2)));
        Assert.Contains("₹3,000.00", await user.GetHtmlAsync($"/Invoices/Details/{invoice}"));

        var tooLarge = await user.RecordPromiseAsync(customer, invoice, 3_000.01m, AppFactory.Today.AddDays(2));
        Assert.Contains("cannot exceed the outstanding balance", WebUtility.HtmlDecode(await tooLarge.Content.ReadAsStringAsync()));

        // After the date passes, the promise shows as unresolved (system-calculated), not as missed.
        // (The promise date is moved into the past directly; moving the clock would also expire the sign-in cookie.)
        await using (var arrange = _factory.CreateDbContext(user.BusinessId))
        {
            await arrange.PromisesToPay.ExecuteUpdateAsync(s => s.SetProperty(p => p.PromisedDate, AppFactory.Today.AddDays(-3)));
        }

        var promisesPage = await user.GetHtmlAsync("/Promises?view=Missed");
        Assert.Contains("Date passed – unresolved", promisesPage);

        int promiseId;
        await using (var db = _factory.CreateDbContext(user.BusinessId))
        {
            var promise = await db.PromisesToPay.SingleAsync();
            Assert.Equal(PromiseStatus.Open, promise.Status);
            promiseId = promise.Id;
        }

        Arrange.AssertRedirect(await user.PostFormAsync($"/Promises/Resolve/{promiseId}", "/Promises/Resolve", new() { ["Id"] = promiseId.ToString(), ["Outcome"] = "Missed", ["ResolutionNotes"] = "No payment received" }));
        var again = await user.PostFormAsync("/Promises", "/Promises/Resolve", new() { ["Id"] = promiseId.ToString(), ["Outcome"] = "Kept" });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Contains("cannot be changed", WebUtility.HtmlDecode(await again.Content.ReadAsStringAsync()));

        await using (var db = _factory.CreateDbContext(user.BusinessId))
        {
            var promise = await db.PromisesToPay.SingleAsync();
            Assert.Equal(PromiseStatus.Missed, promise.Status);
            Assert.NotNull(promise.ResolvedAtUtc);
            Assert.True(await db.FollowUpActivities.AnyAsync(a => a.ActivityType == ActivityType.System && a.Notes.Contains("marked Missed")));
            Assert.Equal(3_000m, (await db.Invoices.SingleAsync()).InvoiceAmount);
            Assert.False(await db.PaymentAllocations.AnyAsync());
        }
    }

    [Fact]
    public async Task Session_expires_after_eight_hours()
    {
        var user = await Browser.RegisterAsync(_factory);
        _factory.Time.UtcNow = AppFactory.FixedNow.AddHours(9);
        try
        {
            var response = await user.GetAsync("/");
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("/Account/Login", response.Headers.Location!.ToString());
        }
        finally
        {
            _factory.Time.UtcNow = AppFactory.FixedNow;
        }
    }

    [Fact]
    public async Task Worklist_orders_by_documented_priority_and_follow_up_notes_are_encoded()
    {
        var user = await Browser.RegisterAsync(_factory);
        var today = AppFactory.Today;
        var c = await user.CreateCustomerAsync("Worklist Co");
        await user.CreateInvoiceAsync(c, "WL-UPCOMING", 1_000m, today.AddDays(5));
        await user.CreateInvoiceAsync(c, "WL-OVERDUE-10", 1_000m, today.AddDays(-10));
        await user.CreateInvoiceAsync(c, "WL-OVERDUE-70", 1_000m, today.AddDays(-70));
        await user.CreateInvoiceAsync(c, "WL-OVERDUE-20", 1_000m, today.AddDays(-20));
        await user.CreateInvoiceAsync(c, "WL-DUE-TODAY", 1_000m, today);

        var html = await user.GetHtmlAsync("/Worklist");
        var order = new[] { "WL-OVERDUE-70", "WL-OVERDUE-20", "WL-OVERDUE-10", "WL-DUE-TODAY", "WL-UPCOMING" }.Select(n => html.IndexOf(n, StringComparison.Ordinal)).ToArray();
        Assert.All(order, i => Assert.True(i > 0));
        Assert.Equal(order.OrderBy(i => i), order);

        var overdueOnly = await user.GetHtmlAsync("/Worklist?Status=Overdue&Bucket=Days1To30");
        Assert.Contains("WL-OVERDUE-10", overdueOnly);
        Assert.DoesNotContain("WL-OVERDUE-70", overdueOnly);
        Assert.DoesNotContain("WL-UPCOMING", overdueOnly);

        var note = await user.PostFormAsync($"/FollowUps/Create?customerId={c}", "/FollowUps/Create", new()
        {
            ["CustomerId"] = c.ToString(), ["ActivityType"] = "Call", ["Notes"] = "<img src=x onerror=alert(1)> called accounts",
            ["NextFollowUpDate"] = today.AddDays(3).ToString("yyyy-MM-dd"), ["ReturnUrl"] = "/Worklist?Status=Overdue",
        });
        Assert.Equal("/Worklist?Status=Overdue", note.Headers.Location!.ToString());

        var raw = await (await user.GetAsync($"/Customers/Details/{c}")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("<img src=x", raw);
        Assert.Contains("&lt;img src=x", raw);

        // Account-level follow-up scheduled in 3 days hides these invoices when "due for follow-up" is selected.
        var due = await user.GetHtmlAsync("/Worklist?DueForFollowUpOnly=true");
        Assert.DoesNotContain("WL-OVERDUE-10", due);
    }

    [Fact]
    public async Task External_return_urls_are_not_followed()
    {
        var user = await Browser.RegisterAsync(_factory);
        var c = await user.CreateCustomerAsync("Redirect Co");

        var response = await user.PostFormAsync($"/FollowUps/Create?customerId={c}", "/FollowUps/Create", new()
        {
            ["CustomerId"] = c.ToString(), ["ActivityType"] = "Note", ["Notes"] = "x", ["ReturnUrl"] = "https://evil.example/phish",
        });

        Assert.Equal($"/Customers/Details/{c}", response.Headers.Location!.ToString());
    }
}
