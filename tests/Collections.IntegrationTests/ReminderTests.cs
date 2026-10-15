using System.Net;
using System.Text.RegularExpressions;
using Collections.Core.Entities;
using Collections.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Collections.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public sealed partial class ReminderTests : IDisposable
{
    private readonly AppFactory _factory;

    public ReminderTests(AppFactory factory)
    {
        _factory = factory;
        _factory.Email.Fail = false;
        _factory.Time.UtcNow = AppFactory.FixedNow;
    }

    public void Dispose()
    {
        _factory.Email.Fail = false;
        _factory.Time.UtcNow = AppFactory.FixedNow;
    }

    [GeneratedRegex("name=\"idempotencyKey\" value=\"([^\"]+)\"")]
    private static partial Regex KeyRegex();

    private static async Task<(string Key, string Html)> PreviewAsync(Browser user, int invoiceId)
    {
        var html = await user.GetHtmlAsync($"/Reminders/Preview?invoiceId={invoiceId}");
        var match = KeyRegex().Match(html);
        return (match.Success ? match.Groups[1].Value : string.Empty, html);
    }

    private static Task<HttpResponseMessage> SendAsync(Browser user, int invoiceId, string key)
        => user.PostFormAsync($"/Reminders/Preview?invoiceId={invoiceId}", "/Reminders/Send", new()
        {
            ["invoiceId"] = invoiceId.ToString(), ["idempotencyKey"] = key,
        });

    private async Task<(Browser User, int CustomerId, int InvoiceId, string Email)> ArrangeAsync(string? email = "")
    {
        var user = await Browser.RegisterAsync(_factory, "Sharma Distributors");
        var recipient = email == string.Empty ? $"ap-{Guid.NewGuid():N}@customer.example" : email;
        var customerId = await user.CreateCustomerAsync("Customer " + Guid.NewGuid().ToString("N")[..6], recipient);
        var invoiceId = await user.CreateInvoiceAsync(customerId, "RM-" + Guid.NewGuid().ToString("N")[..6], 12500m, AppFactory.Today.AddDays(-12));
        return (user, customerId, invoiceId, recipient ?? string.Empty);
    }

    [Fact]
    public async Task Preview_shows_rendered_content_and_send_records_a_sent_log()
    {
        var (user, _, invoiceId, email) = await ArrangeAsync();

        var (key, html) = await PreviewAsync(user, invoiceId);
        Assert.Contains("outstanding balance of ₹12,500.00", html);
        Assert.Contains("due on 03 Oct 2026", html);
        Assert.NotEmpty(key);
        Assert.Equal(0, _factory.Email.CountTo(email)); // preview never sends

        var response = await SendAsync(user, invoiceId, key);
        Arrange.AssertRedirect(response);

        var message = Assert.Single(_factory.Email.Sent, m => m.To == email);
        Assert.Equal("Sharma Distributors", message.FromDisplayName);
        Assert.Equal(user.Email, message.ReplyTo);
        Assert.DoesNotContain("12,500", message.Subject);

        await using var db = _factory.CreateDbContext(user.BusinessId);
        var log = await db.ReminderLogs.SingleAsync();
        Assert.Equal(ReminderStatus.Sent, log.Status);
        Assert.Equal(email, log.Recipient);
        Assert.Equal(1, log.AttemptCount);
        Assert.NotNull(log.ProviderMessageId);
        Assert.Equal(12500m, log.OutstandingAmount);
    }

    [Fact]
    public async Task Resubmitting_the_same_form_or_sending_within_cooldown_does_not_send_again()
    {
        var (user, _, invoiceId, email) = await ArrangeAsync();
        var (key, _) = await PreviewAsync(user, invoiceId);

        Arrange.AssertRedirect(await SendAsync(user, invoiceId, key));
        Arrange.AssertRedirect(await SendAsync(user, invoiceId, key)); // double submit / retry of the same request
        Assert.Equal(1, _factory.Email.CountTo(email));

        var (_, html) = await PreviewAsync(user, invoiceId);
        Assert.Contains("already sent within the last 7 day(s)", html);
        Arrange.AssertRedirect(await SendAsync(user, invoiceId, Guid.NewGuid().ToString("N")));
        Assert.Equal(1, _factory.Email.CountTo(email));

        // Move the sent reminder 7 days into the past (moving the clock would also expire the sign-in cookie).
        await using (var arrange = _factory.CreateDbContext(user.BusinessId))
        {
            await arrange.ReminderLogs.ExecuteUpdateAsync(s => s
                .SetProperty(r => r.SentAtUtc, AppFactory.FixedNow.AddDays(-7))
                .SetProperty(r => r.CreatedAtUtc, AppFactory.FixedNow.AddDays(-7)));
        }

        var (laterKey, _) = await PreviewAsync(user, invoiceId);
        Arrange.AssertRedirect(await SendAsync(user, invoiceId, laterKey));
        Assert.Equal(2, _factory.Email.CountTo(email));

        await using var db = _factory.CreateDbContext(user.BusinessId);
        Assert.Equal(2, await db.ReminderLogs.CountAsync());
    }

    [Fact]
    public async Task Paid_disputed_paused_and_email_less_invoices_are_not_sent()
    {
        // Paid
        var paid = await ArrangeAsync();
        Arrange.AssertRedirect(await paid.User.RecordPaymentAsync(paid.CustomerId, 12500m, AppFactory.Today, (paid.InvoiceId, 12500m)));
        await AssertBlockedAsync(paid.User, paid.InvoiceId, paid.Email, "fully paid");

        // Disputed
        var disputed = await ArrangeAsync();
        Arrange.AssertRedirect(await disputed.User.PostFormAsync($"/Invoices/Details/{disputed.InvoiceId}", $"/Invoices/Flags/{disputed.InvoiceId}", new() { ["disputed"] = "true" }));
        await AssertBlockedAsync(disputed.User, disputed.InvoiceId, disputed.Email, "disputed");

        // Invoice paused
        var invoicePaused = await ArrangeAsync();
        Arrange.AssertRedirect(await invoicePaused.User.PostFormAsync($"/Invoices/Details/{invoicePaused.InvoiceId}", $"/Invoices/Flags/{invoicePaused.InvoiceId}", new() { ["remindersPaused"] = "true" }));
        await AssertBlockedAsync(invoicePaused.User, invoicePaused.InvoiceId, invoicePaused.Email, "paused for this invoice");

        // Customer paused
        var customerPaused = await ArrangeAsync();
        Arrange.AssertRedirect(await customerPaused.User.PostFormAsync($"/Customers/Details/{customerPaused.CustomerId}", $"/Customers/PauseReminders/{customerPaused.CustomerId}", new() { ["paused"] = "true" }));
        await AssertBlockedAsync(customerPaused.User, customerPaused.InvoiceId, customerPaused.Email, "paused for this customer");

        // No email
        var noEmail = await ArrangeAsync(email: null);
        await AssertBlockedAsync(noEmail.User, noEmail.InvoiceId, null, "no email address");
    }

    private async Task AssertBlockedAsync(Browser user, int invoiceId, string? email, string reason)
    {
        var (key, html) = await PreviewAsync(user, invoiceId);
        Assert.Contains(reason, html);
        Assert.Empty(key); // no send button rendered

        // Even a hand-crafted POST is refused by the server.
        var before = _factory.Email.Sent.Count;
        var response = await SendAsync(user, invoiceId, Guid.NewGuid().ToString("N"));
        Arrange.AssertRedirect(response);
        Assert.Contains("/Reminders/Preview", response.Headers.Location!.ToString());
        Assert.Equal(before, _factory.Email.Sent.Count);
        if (email is not null)
        {
            Assert.Equal(0, _factory.Email.CountTo(email));
        }

        await using var db = _factory.CreateDbContext(user.BusinessId);
        Assert.False(await db.ReminderLogs.AnyAsync());
    }

    [Fact]
    public async Task Failed_delivery_is_recorded_visible_and_can_be_retried_once()
    {
        var (user, _, invoiceId, email) = await ArrangeAsync();
        var (key, _) = await PreviewAsync(user, invoiceId);

        _factory.Email.Fail = true;
        var response = await SendAsync(user, invoiceId, key);
        var logId = Browser.IdFromLocation(response);

        var details = await user.GetHtmlAsync($"/Reminders/Details/{logId}");
        Assert.Contains("Failed", details);
        Assert.Contains("The mail server rejected the message (SMTP 550).", details);
        Assert.Contains("SMTP 550", await user.GetHtmlAsync("/Reminders?status=Failed"));

        await using (var db = _factory.CreateDbContext(user.BusinessId))
        {
            var log = await db.ReminderLogs.SingleAsync();
            Assert.Equal(ReminderStatus.Failed, log.Status);
            Assert.Equal("The mail server rejected the message (SMTP 550).", log.LastError);
        }

        // A failed attempt does not start the cooldown, and the same form cannot create a second log.
        Arrange.AssertRedirect(await SendAsync(user, invoiceId, key));
        await using (var db = _factory.CreateDbContext(user.BusinessId))
        {
            Assert.Equal(1, await db.ReminderLogs.CountAsync());
        }

        _factory.Email.Fail = false;
        Arrange.AssertRedirect(await user.PostFormAsync($"/Reminders/Details/{logId}", $"/Reminders/Retry/{logId}", new()));
        Arrange.AssertRedirect(await user.PostFormAsync($"/Reminders/Details/{logId}", $"/Reminders/Retry/{logId}", new()));

        Assert.Equal(1, _factory.Email.CountTo(email));
        await using (var db = _factory.CreateDbContext(user.BusinessId))
        {
            var log = await db.ReminderLogs.SingleAsync();
            Assert.Equal(ReminderStatus.Sent, log.Status);
            Assert.Equal(2, log.AttemptCount);
            Assert.Null(log.LastError);
        }
    }

    [Fact]
    public async Task Template_variables_are_html_encoded_in_preview()
    {
        var user = await Browser.RegisterAsync(_factory);
        var customerId = await user.CreateCustomerAsync("<script>alert('x')</script> Traders", "xss@customer.example");
        var invoiceId = await user.CreateInvoiceAsync(customerId, "XSS-1", 100m, AppFactory.Today);

        var raw = await (await user.GetAsync($"/Reminders/Preview?invoiceId={invoiceId}")).Content.ReadAsStringAsync();

        Assert.DoesNotContain("<script>alert", raw);
        Assert.Contains("&lt;script&gt;", raw);
    }
}
