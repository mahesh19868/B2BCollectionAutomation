using Collections.Core.Financial;
using Collections.Core.Formatting;
using Collections.Core.Reminders;

namespace Collections.UnitTests.Reminders;

public class ReminderEligibilityTests
{
    private static readonly DateOnly AsOf = new(2026, 10, 15);
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 6, 0, 0, TimeSpan.Zero);

    private static ReminderEligibilityInput Eligible() => new(
        CustomerEmail: "accounts@example.com",
        CustomerIsActive: true,
        CustomerRemindersPaused: false,
        InvoiceIsDisputed: false,
        InvoiceRemindersPaused: false,
        Financials: InvoiceCalculator.Calculate(1000m, 0m, AsOf.AddDays(-10), AsOf),
        LastSentAtUtc: null,
        HasSendInProgress: false,
        CooldownDays: 7,
        NowUtc: Now);

    [Fact]
    public void Eligible_invoice_has_no_reasons()
    {
        Assert.Empty(ReminderEligibility.Evaluate(Eligible()));
    }

    [Fact]
    public void Paid_invoice_is_not_eligible()
    {
        var input = Eligible() with { Financials = InvoiceCalculator.Calculate(1000m, 1000m, AsOf.AddDays(-10), AsOf) };

        Assert.Contains(ReminderEligibility.Evaluate(input), r => r.Contains("fully paid"));
    }

    [Fact]
    public void Disputed_invoice_is_not_eligible()
    {
        Assert.Contains(ReminderEligibility.Evaluate(Eligible() with { InvoiceIsDisputed = true }), r => r.Contains("disputed"));
    }

    [Fact]
    public void Paused_customer_or_invoice_is_not_eligible()
    {
        Assert.Contains(ReminderEligibility.Evaluate(Eligible() with { CustomerRemindersPaused = true }), r => r.Contains("paused for this customer"));
        Assert.Contains(ReminderEligibility.Evaluate(Eligible() with { InvoiceRemindersPaused = true }), r => r.Contains("paused for this invoice"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-email")]
    [InlineData("a@b")]
    [InlineData("Name <a@example.com>")]
    public void Missing_or_invalid_email_is_not_eligible(string? email)
    {
        Assert.Contains(ReminderEligibility.Evaluate(Eligible() with { CustomerEmail = email }), r => r.Contains("email"));
    }

    [Fact]
    public void Inactive_customer_and_send_in_progress_are_not_eligible()
    {
        Assert.NotEmpty(ReminderEligibility.Evaluate(Eligible() with { CustomerIsActive = false }));
        Assert.NotEmpty(ReminderEligibility.Evaluate(Eligible() with { HasSendInProgress = true }));
    }

    [Fact]
    public void Cooldown_blocks_repeat_reminders_until_it_expires()
    {
        Assert.Contains(ReminderEligibility.Evaluate(Eligible() with { LastSentAtUtc = Now.AddDays(-6) }), r => r.Contains("already sent"));
        Assert.Empty(ReminderEligibility.Evaluate(Eligible() with { LastSentAtUtc = Now.AddDays(-7) }));
    }
}

public class ReminderTemplateRendererTests
{
    private static Dictionary<string, string> Values(string customerName = "Acme <b>Ltd</b>") => ReminderEligibility.BuildTemplateValues(
        customerName, "INV-1", new DateOnly(2026, 4, 1), new DateOnly(2026, 5, 1), 150000m, 125000m, "Sharma Distributors", "accounts@sharma.example");

    [Fact]
    public void Default_template_renders_all_values_with_inr_and_date_formatting()
    {
        var body = ReminderTemplateRenderer.Render(ReminderTemplateDefaults.Body, Values());
        var subject = ReminderTemplateRenderer.RenderSubject(ReminderTemplateDefaults.Subject, Values());

        Assert.Equal("Payment reminder for invoice INV-1", subject);
        Assert.Contains("outstanding balance of ₹1,25,000.00 for invoice INV-1, due on 01 May 2026", body);
        Assert.Contains("contact Sharma Distributors at accounts@sharma.example", body);
        Assert.DoesNotContain("{{", body);
    }

    [Fact]
    public void Values_are_inserted_literally_without_nested_expansion()
    {
        var body = ReminderTemplateRenderer.Render("Hello {{CustomerName}}", Values("{{BusinessName}}"));

        Assert.Equal("Hello {{BusinessName}}", body);
    }

    [Fact]
    public void Subject_cannot_contain_line_breaks()
    {
        var subject = ReminderTemplateRenderer.RenderSubject("Reminder {{CustomerName}}", Values("Acme\r\nBcc: victim@example.com"));

        Assert.DoesNotContain('\r', subject);
        Assert.DoesNotContain('\n', subject);
    }

    [Fact]
    public void Unknown_placeholders_are_rejected()
    {
        var errors = ReminderTemplateRenderer.Validate("Hi {{CustomerName}} {{Password}}", 1000);

        Assert.Contains(errors, e => e.Contains("{{Password}}"));
        Assert.Empty(ReminderTemplateRenderer.Validate(ReminderTemplateDefaults.Body, ReminderTemplateRenderer.BodyMaxLength));
    }
}

public class FormattingTests
{
    [Theory]
    [InlineData("125000", "₹1,25,000.00")]
    [InlineData("12345678.9", "₹1,23,45,678.90")]
    [InlineData("999", "₹999.00")]
    [InlineData("0", "₹0.00")]
    [InlineData("-500", "-₹500.00")]
    public void Inr_uses_indian_grouping(string amount, string expected)
    {
        Assert.Equal(expected, MoneyFormat.Inr(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void Dates_display_as_dd_mmm_yyyy()
    {
        Assert.Equal("05 Oct 2026", DateFormat.Display(new DateOnly(2026, 10, 5)));
    }
}
