using System.Globalization;
using System.Net;

namespace Collections.IntegrationTests.Infrastructure;

/// <summary>Creates synthetic test data through the real web forms.</summary>
public static class Arrange
{
    public static async Task<int> CreateCustomerAsync(this Browser browser, string name, string? email = null, string? code = null)
    {
        var fields = new Dictionary<string, string> { ["Name"] = name, ["IsActive"] = "true" };
        if (email is not null) fields["Email"] = email;
        if (code is not null) fields["CustomerCode"] = code;

        var response = await browser.PostFormAsync("/Customers/Create", "/Customers/Create", fields);
        return Browser.IdFromLocation(response);
    }

    public static async Task<int> CreateInvoiceAsync(this Browser browser, int customerId, string number, decimal amount, DateOnly dueDate, DateOnly? invoiceDate = null)
    {
        var response = await browser.PostFormAsync("/Invoices/Create", "/Invoices/Create", new()
        {
            ["CustomerId"] = customerId.ToString(CultureInfo.InvariantCulture),
            ["InvoiceNumber"] = number,
            ["InvoiceDate"] = (invoiceDate ?? dueDate.AddDays(-30)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["DueDate"] = dueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["InvoiceAmount"] = amount.ToString(CultureInfo.InvariantCulture),
        });
        return Browser.IdFromLocation(response);
    }

    public static async Task<HttpResponseMessage> RecordPaymentAsync(this Browser browser, int customerId, decimal amount, DateOnly date,
        params (int InvoiceId, decimal Amount)[] allocations)
    {
        var fields = new Dictionary<string, string>
        {
            ["CustomerId"] = customerId.ToString(CultureInfo.InvariantCulture),
            ["PaymentDate"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["Amount"] = amount.ToString(CultureInfo.InvariantCulture),
        };
        for (var i = 0; i < allocations.Length; i++)
        {
            fields[$"Allocations[{i}].InvoiceId"] = allocations[i].InvoiceId.ToString(CultureInfo.InvariantCulture);
            fields[$"Allocations[{i}].Amount"] = allocations[i].Amount.ToString(CultureInfo.InvariantCulture);
        }

        return await browser.PostFormAsync($"/Payments/Create?customerId={customerId}", "/Payments/Create", fields);
    }

    public static async Task<HttpResponseMessage> RecordPromiseAsync(this Browser browser, int customerId, int? invoiceId, decimal amount, DateOnly date)
    {
        var fields = new Dictionary<string, string>
        {
            ["CustomerId"] = customerId.ToString(CultureInfo.InvariantCulture),
            ["PromisedAmount"] = amount.ToString(CultureInfo.InvariantCulture),
            ["PromisedDate"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["Notes"] = "Promised on phone",
        };
        if (invoiceId is not null) fields["InvoiceId"] = invoiceId.Value.ToString(CultureInfo.InvariantCulture);

        var page = invoiceId is null ? $"/Promises/Create?customerId={customerId}" : $"/Promises/Create?customerId={customerId}&invoiceId={invoiceId}";
        return await browser.PostFormAsync(page, "/Promises/Create", fields);
    }

    public static void AssertRedirect(HttpResponseMessage response) => Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
}
