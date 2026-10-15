using System.Net;
using Collections.Core.Entities;
using Collections.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Collections.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public sealed class SecurityTests
{
    private readonly AppFactory _factory;

    public SecurityTests(AppFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/Worklist")]
    [InlineData("/Customers")]
    [InlineData("/Invoices/Details/1")]
    [InlineData("/Import")]
    [InlineData("/Reminders")]
    [InlineData("/Settings")]
    public async Task Unauthenticated_users_are_redirected_to_sign_in(string url)
    {
        var response = await _factory.CreateBrowser().GetAsync(url);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task State_changing_post_without_antiforgery_token_is_rejected()
    {
        var user = await Browser.RegisterAsync(_factory);

        var response = await user.Client.PostAsync("/Customers/Create", new FormUrlEncodedContent(new Dictionary<string, string> { ["Name"] = "No Token Ltd" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var db = _factory.CreateDbContext(user.BusinessId);
        Assert.False(await db.Customers.AnyAsync(c => c.Name == "No Token Ltd"));
    }

    [Fact]
    public async Task Business_B_cannot_read_or_change_business_A_records()
    {
        var a = await Browser.RegisterAsync(_factory);
        var b = await Browser.RegisterAsync(_factory);

        var customerId = await a.CreateCustomerAsync("Alpha Secret Traders", "alpha@example.com");
        var invoiceId = await a.CreateInvoiceAsync(customerId, "A-SECRET-1", 5000m, AppFactory.Today.AddDays(-10));
        var paymentId = Browser.IdFromLocation(await a.RecordPaymentAsync(customerId, 1000m, AppFactory.Today, (invoiceId, 1000m)));
        Arrange.AssertRedirect(await a.RecordPromiseAsync(customerId, invoiceId, 2000m, AppFactory.Today.AddDays(3)));
        Assert.Equal(HttpStatusCode.Redirect, (await a.UploadAsync("a.csv", "CustomerName,InvoiceNumber,InvoiceDate,DueDate,InvoiceAmount\nAlpha Secret Traders,A-SECRET-2,2026-09-01,2026-10-01,100\n")).StatusCode);

        int promiseId, batchId;
        await using (var db = _factory.CreateDbContext(a.BusinessId))
        {
            promiseId = await db.PromisesToPay.Select(p => p.Id).SingleAsync();
            batchId = await db.ImportBatches.Select(x => x.Id).SingleAsync();
            db.ReminderLogs.Add(new ReminderLog { CustomerId = customerId, InvoiceId = invoiceId, TemplateKey = "t", IdempotencyKey = Guid.NewGuid().ToString("N"), Recipient = "alpha@example.com", Subject = "s", Body = "b", Status = ReminderStatus.Failed, CreatedAtUtc = AppFactory.FixedNow });
            await db.SaveChangesAsync();
        }

        int reminderId;
        await using (var db = _factory.CreateDbContext(a.BusinessId))
        {
            reminderId = await db.ReminderLogs.Select(r => r.Id).SingleAsync();
        }

        // Reads
        foreach (var url in new[]
                 {
                     $"/Customers/Details/{customerId}", $"/Customers/Edit/{customerId}", $"/Invoices/Details/{invoiceId}", $"/Invoices/Edit/{invoiceId}",
                     $"/Payments/Details/{paymentId}", $"/Payments/Delete/{paymentId}", $"/Promises/Resolve/{promiseId}", $"/Import/Batch/{batchId}",
                     $"/Import/Errors/{batchId}", $"/Reminders/Details/{reminderId}", $"/Reminders/Preview?invoiceId={invoiceId}",
                     $"/FollowUps/Create?customerId={customerId}", $"/Payments/Create?customerId={customerId}",
                 })
        {
            var response = await b.GetAsync(url);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{url} returned {(int)response.StatusCode} for another business");
        }

        Assert.DoesNotContain("A-SECRET-1", await b.GetHtmlAsync("/Invoices?Status=All"));
        Assert.DoesNotContain("Alpha Secret Traders", await b.GetHtmlAsync("/Customers"));
        Assert.DoesNotContain("A-SECRET-1", await b.GetHtmlAsync("/Worklist"));

        // Writes (B uses its own valid antiforgery token from its own pages)
        var bCustomer = await b.CreateCustomerAsync("Beta Own Customer");
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostFormAsync("/Customers/Create", $"/Customers/Edit/{customerId}", new() { ["Name"] = "Hacked" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostFormAsync("/Customers/Create", $"/Invoices/Flags/{invoiceId}", new() { ["disputed"] = "true" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostFormAsync("/Customers/Create", $"/Payments/Delete/{paymentId}", new())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostFormAsync("/Customers/Create", "/Promises/Resolve", new() { ["Id"] = promiseId.ToString(), ["Outcome"] = "Missed" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostFormAsync("/Customers/Create", $"/Reminders/Retry/{reminderId}", new())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostFormAsync("/Customers/Create", $"/Import/Confirm/{batchId}", new())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostFormAsync("/Customers/Create", "/Reminders/Send", new() { ["invoiceId"] = invoiceId.ToString(), ["idempotencyKey"] = Guid.NewGuid().ToString("N") })).StatusCode);

        // B cannot attach an invoice, payment allocation or promise to A's customer/invoice.
        var invoiceAttempt = await b.PostFormAsync("/Invoices/Create", "/Invoices/Create", new()
        {
            ["CustomerId"] = customerId.ToString(), ["InvoiceNumber"] = "B-INTO-A", ["InvoiceDate"] = "2026-10-01", ["DueDate"] = "2026-10-30", ["InvoiceAmount"] = "10",
        });
        Assert.Equal(HttpStatusCode.OK, invoiceAttempt.StatusCode); // redisplayed with a validation error
        Assert.Equal(HttpStatusCode.OK, (await b.RecordPaymentAsync(bCustomer, 100m, AppFactory.Today, (invoiceId, 100m))).StatusCode);

        // A's data is unchanged.
        await using (var db = _factory.CreateDbContext(a.BusinessId))
        {
            Assert.Equal("Alpha Secret Traders", (await db.Customers.SingleAsync(c => c.Id == customerId)).Name);
            Assert.False((await db.Invoices.SingleAsync(i => i.Id == invoiceId)).IsDisputed);
            Assert.True(await db.Payments.AnyAsync(p => p.Id == paymentId));
            Assert.Equal(PromiseStatus.Open, (await db.PromisesToPay.SingleAsync()).Status);
            Assert.Equal(ImportBatchStatus.Previewed, (await db.ImportBatches.SingleAsync()).Status);
            Assert.Equal(1000m, await db.PaymentAllocations.SumAsync(x => x.AllocatedAmount));
        }

        await using (var db = _factory.CreateDbContext(b.BusinessId))
        {
            Assert.False(await db.Invoices.AnyAsync(i => i.InvoiceNumber == "B-INTO-A"));
            Assert.False(await db.Payments.AnyAsync());
        }
    }

    [Fact]
    public async Task Client_supplied_business_id_is_ignored()
    {
        var a = await Browser.RegisterAsync(_factory);
        var b = await Browser.RegisterAsync(_factory);

        var response = await b.PostFormAsync("/Customers/Create", "/Customers/Create", new()
        {
            ["Name"] = "Injected Customer", ["BusinessId"] = a.BusinessId.ToString(), ["IsActive"] = "true",
        });
        Arrange.AssertRedirect(response);

        await using var aDb = _factory.CreateDbContext(a.BusinessId);
        await using var bDb = _factory.CreateDbContext(b.BusinessId);
        Assert.False(await aDb.Customers.AnyAsync(c => c.Name == "Injected Customer"));
        Assert.Equal(b.BusinessId, (await bDb.Customers.SingleAsync(c => c.Name == "Injected Customer")).BusinessId);
    }

    [Fact]
    public async Task Data_layer_refuses_to_write_another_business_records()
    {
        var a = await Browser.RegisterAsync(_factory);
        var b = await Browser.RegisterAsync(_factory);
        var customerId = await a.CreateCustomerAsync("Guarded Customer");

        await using var bDb = _factory.CreateDbContext(b.BusinessId);
        bDb.Customers.Add(new Customer { BusinessId = a.BusinessId, Name = "Forged" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => bDb.SaveChangesAsync());

        bDb.ChangeTracker.Clear();
        bDb.Customers.Attach(new Customer { Id = customerId, BusinessId = a.BusinessId, Name = "Guarded Customer" }).Property(c => c.Name).CurrentValue = "Overwritten";
        await Assert.ThrowsAsync<InvalidOperationException>(() => bDb.SaveChangesAsync());

        await using var noTenant = _factory.CreateDbContext(null);
        Assert.Empty(await noTenant.Customers.ToListAsync());
    }

    [Fact]
    public async Task Responses_include_security_headers_and_secure_cookies()
    {
        var response = await _factory.CreateBrowser().GetAsync("/Account/Login");

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("script-src 'self'", response.Headers.GetValues("Content-Security-Policy").Single());
        var cookies = response.Headers.GetValues("Set-Cookie").ToList();
        Assert.All(cookies, c => Assert.True(c.Contains("secure", StringComparison.OrdinalIgnoreCase), "Cookie without Secure flag: " + c.Split(';')[0].Split('=')[0]));
    }

    [Fact]
    public async Task Repeated_failed_sign_ins_lock_the_account()
    {
        var user = await Browser.RegisterAsync(_factory);
        var anonymous = _factory.CreateBrowser();
        HttpResponseMessage? last = null;
        for (var i = 0; i < 6; i++)
        {
            last = await PostLoginAsync(anonymous, user.Email, "WrongPassword123");
        }

        var html = await last!.Content.ReadAsStringAsync();
        Assert.Contains("Too many failed attempts", html);
        Assert.Equal(HttpStatusCode.OK, (await PostLoginAsync(anonymous, user.Email, "Sup3rSecretPass")).StatusCode); // still locked: form redisplayed
    }

    private static async Task<HttpResponseMessage> PostLoginAsync(HttpClient client, string email, string password)
    {
        var html = await client.GetStringAsync("/Account/Login");
        var token = System.Text.RegularExpressions.Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        return await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email, ["Password"] = password, ["__RequestVerificationToken"] = token,
        }));
    }
}
