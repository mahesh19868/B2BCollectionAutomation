using System.Net;
using Collections.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Collections.IntegrationTests;

/// <summary>Renders every page for empty and populated workspaces to catch view/runtime errors.</summary>
[Collection(IntegrationCollection.Name)]
public sealed class PageSmokeTests
{
    private readonly AppFactory _factory;

    public PageSmokeTests(AppFactory factory)
    {
        _factory = factory;
        _factory.Time.UtcNow = AppFactory.FixedNow;
    }

    [Fact]
    public async Task Empty_workspace_pages_render_with_helpful_empty_states()
    {
        var user = await Browser.RegisterAsync(_factory);

        Assert.Contains("No invoices yet", await user.GetHtmlAsync("/"));
        Assert.Contains("No customers yet", await user.GetHtmlAsync("/Customers"));
        Assert.Contains("Nothing to follow up", await user.GetHtmlAsync("/Worklist"));
        Assert.Contains("No invoices match", await user.GetHtmlAsync("/Invoices"));
        Assert.Contains("No payments found", await user.GetHtmlAsync("/Payments"));
        Assert.Contains("no active customers", await user.GetHtmlAsync("/Payments/Create"));
        Assert.Contains("No promises", await user.GetHtmlAsync("/Promises"));
        Assert.Contains("No reminders", await user.GetHtmlAsync("/Reminders"));
        Assert.Contains("No imports yet", await user.GetHtmlAsync("/Import"));
        Assert.Contains("Add a customer first", await user.GetHtmlAsync("/Invoices/Create"));
        Assert.Contains("Payment reminder for invoice {{InvoiceNumber}}", await user.GetHtmlAsync("/Settings"));
    }

    [Fact]
    public async Task Populated_workspace_pages_render()
    {
        var user = await Browser.RegisterAsync(_factory);
        var c = await user.CreateCustomerAsync("Smoke & Sons <Ltd>", "smoke@customer.example", "SM1");
        var inv = await user.CreateInvoiceAsync(c, "SM-1", 1234.5m, AppFactory.Today.AddDays(-3));
        var paymentId = Browser.IdFromLocation(await user.RecordPaymentAsync(c, 200m, AppFactory.Today, (inv, 100m)));
        Arrange.AssertRedirect(await user.RecordPromiseAsync(c, null, 500m, AppFactory.Today.AddDays(1)));
        var batch = Browser.IdFromLocation(await user.UploadAsync("x.csv", "CustomerName,InvoiceNumber,InvoiceDate,DueDate,InvoiceAmount\nSmoke Two,SM-2,2026-09-01,2026-10-01,50\n"));

        int promiseId;
        await using (var db = _factory.CreateDbContext(user.BusinessId))
        {
            promiseId = await db.PromisesToPay.Select(p => p.Id).SingleAsync();
        }

        foreach (var url in new[]
                 {
                     "/", "/Worklist", "/Worklist?Status=Overdue&Promise=Open&MinAmount=10&page=2", "/Customers", "/Customers?search=smoke",
                     $"/Customers/Details/{c}", $"/Customers/Edit/{c}", "/Customers/Create",
                     "/Invoices", "/Invoices?Status=All&Bucket=Days1To30", $"/Invoices/Details/{inv}", $"/Invoices/Edit/{inv}", $"/Invoices/Create?customerId={c}",
                     "/Payments", $"/Payments/Create?customerId={c}", $"/Payments/Details/{paymentId}", $"/Payments/Delete/{paymentId}",
                     "/Promises", "/Promises?view=All", "/Promises?view=Resolved", $"/Promises/Create?customerId={c}&invoiceId={inv}", $"/Promises/Resolve/{promiseId}",
                     $"/FollowUps/Create?customerId={c}&invoiceId={inv}",
                     "/Reminders", "/Reminders?status=Sent", $"/Reminders/Preview?invoiceId={inv}",
                     "/Import", $"/Import/Batch/{batch}", "/Settings",
                 })
        {
            var response = await user.GetAsync(url);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} returned {(int)response.StatusCode}");
            var html = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("<Ltd>", html); // user content is always encoded
        }

        var template = await user.GetAsync("/Import/Template");
        Assert.Equal("text/csv", template.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await user.GetAsync("/Customers/Details/999999")).StatusCode);
    }

    [Fact]
    public async Task Settings_validate_and_save_templates_and_edits_are_recorded()
    {
        var user = await Browser.RegisterAsync(_factory);

        var bad = await user.PostFormAsync("/Settings", "/Settings", new()
        {
            ["Name"] = "Renamed Distributors", ["ContactEmail"] = "accounts@renamed.example", ["ReminderCooldownDays"] = "5",
            ["ReminderSubjectTemplate"] = "Hi {{Password}}", ["ReminderBodyTemplate"] = "Body {{CustomerName}}",
        });
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        Assert.Contains("Unknown placeholder {{Password}}", WebUtility.HtmlDecode(await bad.Content.ReadAsStringAsync()));

        Arrange.AssertRedirect(await user.PostFormAsync("/Settings", "/Settings", new()
        {
            ["Name"] = "Renamed Distributors", ["ContactEmail"] = "accounts@renamed.example", ["ReminderCooldownDays"] = "5",
            ["ReminderSubjectTemplate"] = "Invoice {{InvoiceNumber}} reminder", ["ReminderBodyTemplate"] = "Dear {{CustomerName}}, {{OutstandingAmount}} is due. {{BusinessName}}",
        }));

        var c = await user.CreateCustomerAsync("Template Co", "t@customer.example");
        var inv = await user.CreateInvoiceAsync(c, "TP-1", 999m, AppFactory.Today);
        var preview = await user.GetHtmlAsync($"/Reminders/Preview?invoiceId={inv}");
        Assert.Contains("Invoice TP-1 reminder", preview);
        Assert.Contains("Dear Template Co, ₹999.00 is due. Renamed Distributors", preview);

        // Correcting an invoice is recorded in its history.
        Arrange.AssertRedirect(await user.PostFormAsync($"/Invoices/Edit/{inv}", $"/Invoices/Edit/{inv}", new()
        {
            ["CustomerId"] = c.ToString(), ["InvoiceDate"] = "2026-10-01", ["DueDate"] = "2026-10-20", ["InvoiceAmount"] = "1000",
        }));
        Assert.Contains("amount ₹999.00 → ₹1,000.00", await user.GetHtmlAsync($"/Invoices/Details/{inv}"));
    }
}
