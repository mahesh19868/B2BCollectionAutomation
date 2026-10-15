using Collections.Core.Entities;
using Collections.Core.Imports;

namespace Collections.UnitTests.Imports;

public class InvoiceImportPlannerTests
{
    private static InvoiceImportRawRow Row(int rowNumber, string? number = "INV-1", string? name = "Acme", string? amount = "1000",
        string? invoiceDate = "2026-04-01", string? dueDate = "2026-05-01", string? code = null, string? email = null)
        => new(rowNumber, code, name, email, number, invoiceDate, dueDate, amount);

    private static IReadOnlyList<PlannedInvoiceRow> Plan(IReadOnlyList<InvoiceImportRawRow> rows,
        ExistingCustomer[]? customers = null, ExistingInvoice[]? invoices = null)
        => InvoiceImportPlanner.Plan(rows, customers ?? Array.Empty<ExistingCustomer>(), invoices ?? Array.Empty<ExistingInvoice>());

    [Fact]
    public void Valid_new_rows_are_planned_as_create_with_parsed_values()
    {
        var plan = Plan(new[] { Row(2, amount: "1,25,000.50", invoiceDate: "15-Apr-2026", dueDate: "15/05/2026") });

        var row = Assert.Single(plan);
        Assert.Equal(ImportRowAction.Create, row.Action);
        Assert.Equal(125000.50m, row.InvoiceAmount);
        Assert.Equal(new DateOnly(2026, 4, 15), row.InvoiceDate);
        Assert.Equal(new DateOnly(2026, 5, 15), row.DueDate);
        Assert.NotNull(row.NewCustomerKey);
    }

    [Theory]
    [InlineData("2026-13-01")]
    [InlineData("31/02/2026")]
    [InlineData("04/15/2026")]
    [InlineData("yesterday")]
    public void Malformed_dates_are_rejected_with_field_and_row(string date)
    {
        var row = Assert.Single(Plan(new[] { Row(7, invoiceDate: date) }));

        Assert.Equal(ImportRowAction.Error, row.Action);
        Assert.Equal(7, row.Raw.RowNumber);
        Assert.Contains(row.Errors, e => e.Field == "InvoiceDate" && e.Message.Contains("not a valid date"));
    }

    [Theory]
    [InlineData("12.345", "at most 2 decimal")]
    [InlineData("1.2.3", "not a valid amount")]
    [InlineData("₹100", "not a valid amount")]
    [InlineData("1e5", "not a valid amount")]
    [InlineData("100.00,5", "not a valid amount")]
    [InlineData("0", "greater than zero")]
    [InlineData("-50", "cannot be negative")]
    [InlineData(null, "required")]
    public void Malformed_zero_and_negative_amounts_are_rejected(string? amount, string expected)
    {
        var row = Assert.Single(Plan(new[] { Row(2, amount: amount) }));

        Assert.Equal(ImportRowAction.Error, row.Action);
        Assert.Contains(row.Errors, e => e.Field == "InvoiceAmount" && e.Message.Contains(expected));
    }

    [Fact]
    public void Due_date_before_invoice_date_is_rejected()
    {
        var row = Assert.Single(Plan(new[] { Row(2, invoiceDate: "2026-05-02", dueDate: "2026-05-01") }));

        Assert.Contains(row.Errors, e => e.Field == "DueDate");
    }

    [Fact]
    public void Missing_required_values_and_bad_email_are_all_reported()
    {
        var row = Assert.Single(Plan(new[] { Row(2, number: null, name: null, email: "not-an-email") }));

        Assert.Contains(row.Errors, e => e.Field == "InvoiceNumber");
        Assert.Contains(row.Errors, e => e.Field == "CustomerName");
        Assert.Contains(row.Errors, e => e.Field == "CustomerEmail");
    }

    [Fact]
    public void Duplicate_invoice_within_file_rejects_every_occurrence()
    {
        var plan = Plan(new[] { Row(2, number: "INV-1"), Row(3, number: "INV-2"), Row(4, number: "inv-1") });

        Assert.Equal(ImportRowAction.Error, plan[0].Action);
        Assert.Equal(ImportRowAction.Create, plan[1].Action);
        Assert.Equal(ImportRowAction.Error, plan[2].Action);
        Assert.Contains("rows 2, 4", plan[0].Errors.Single().Message);
    }

    [Fact]
    public void Existing_identical_invoice_is_unchanged_so_reimport_does_not_duplicate()
    {
        var customers = new[] { new ExistingCustomer(10, null, "Acme", null) };
        var invoices = new[] { new ExistingInvoice(100, "INV-1", 10, "Acme", new(2026, 4, 1), new(2026, 5, 1), 1000m, 0m) };

        var row = Assert.Single(Plan(new[] { Row(2) }, customers, invoices));

        Assert.Equal(ImportRowAction.Unchanged, row.Action);
        Assert.Equal(100, row.ExistingInvoiceId);
    }

    [Fact]
    public void Existing_invoice_with_changes_is_planned_as_update_with_description()
    {
        var customers = new[] { new ExistingCustomer(10, null, "Acme", null) };
        var invoices = new[] { new ExistingInvoice(100, "INV-1", 10, "Acme", new(2026, 4, 1), new(2026, 5, 1), 900m, 0m) };

        var row = Assert.Single(Plan(new[] { Row(2) }, customers, invoices));

        Assert.Equal(ImportRowAction.Update, row.Action);
        Assert.Contains(row.Notes, n => n.Contains("₹900.00 → ₹1,000.00"));
    }

    [Fact]
    public void Update_below_allocated_payments_is_rejected()
    {
        var customers = new[] { new ExistingCustomer(10, null, "Acme", null) };
        var invoices = new[] { new ExistingInvoice(100, "INV-1", 10, "Acme", new(2026, 4, 1), new(2026, 5, 1), 2000m, 1500m) };

        var row = Assert.Single(Plan(new[] { Row(2, amount: "1000") }, customers, invoices));

        Assert.Equal(ImportRowAction.Error, row.Action);
        Assert.Contains(row.Errors, e => e.Message.Contains("less than payments already allocated"));
    }

    [Fact]
    public void Existing_invoice_number_for_another_customer_is_rejected()
    {
        var customers = new[] { new ExistingCustomer(10, null, "Acme", null), new ExistingCustomer(11, null, "Beta", null) };
        var invoices = new[] { new ExistingInvoice(100, "INV-1", 11, "Beta", new(2026, 4, 1), new(2026, 5, 1), 1000m, 0m) };

        var row = Assert.Single(Plan(new[] { Row(2) }, customers, invoices));

        Assert.Equal(ImportRowAction.Error, row.Action);
        Assert.Contains(row.Errors, e => e.Message.Contains("already exists for customer 'Beta'"));
    }

    [Fact]
    public void Customer_matched_by_code_keeps_existing_name()
    {
        var customers = new[] { new ExistingCustomer(10, "C1", "Acme Traders", "old@example.com") };

        var row = Assert.Single(Plan(new[] { Row(2, code: "c1", name: "Acme", email: "new@example.com") }, customers));

        Assert.Equal(10, row.ExistingCustomerId);
        Assert.Contains(row.Notes, n => n.Contains("existing name is kept"));
        Assert.Contains(row.Notes, n => n.Contains("saved email is kept"));
    }

    [Fact]
    public void Ambiguous_customer_name_without_code_is_rejected()
    {
        var customers = new[] { new ExistingCustomer(10, "A", "Acme", null), new ExistingCustomer(11, "B", "ACME", null) };

        var row = Assert.Single(Plan(new[] { Row(2) }, customers));

        Assert.Equal(ImportRowAction.Error, row.Action);
        Assert.Contains(row.Errors, e => e.Message.Contains("Add a CustomerCode"));
    }

    [Fact]
    public void Same_code_with_different_names_in_file_is_rejected()
    {
        var plan = Plan(new[] { Row(2, number: "1", code: "C1", name: "Acme"), Row(3, number: "2", code: "C1", name: "Other") });

        Assert.All(plan, r => Assert.Equal(ImportRowAction.Error, r.Action));
    }

    [Fact]
    public void Rows_for_the_same_new_customer_share_one_customer_key()
    {
        var plan = Plan(new[] { Row(2, number: "1", email: "a@example.com"), Row(3, number: "2", name: " acme ") });

        Assert.Equal(plan[0].NewCustomerKey, plan[1].NewCustomerKey);
        Assert.Equal("a@example.com", InvoiceImportPlanner.EmailForNewCustomer(plan, plan[0].NewCustomerKey!));
    }

    [Fact]
    public void Invoice_number_is_trimmed_but_otherwise_unchanged()
    {
        var row = Assert.Single(Plan(new[] { Row(2, number: "  inv/001-A  ") }));

        Assert.Equal("inv/001-A", row.InvoiceNumber);
    }
}
