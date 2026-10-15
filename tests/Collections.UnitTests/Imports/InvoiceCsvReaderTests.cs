using System.Text;
using Collections.Core.Imports;

namespace Collections.UnitTests.Imports;

public class InvoiceCsvReaderTests
{
    private const string Header = "CustomerCode,CustomerName,CustomerEmail,InvoiceNumber,InvoiceDate,DueDate,InvoiceAmount";
    private static readonly ImportLimits Limits = new() { MaxFileBytes = 10_000, MaxRows = 3 };

    private static InvoiceFileReadResult Read(string text, string fileName = "invoices.csv", ImportLimits? limits = null)
        => InvoiceCsvReader.Read(Encoding.UTF8.GetBytes(text), fileName, limits ?? Limits);

    [Fact]
    public void Valid_file_returns_rows_with_spreadsheet_row_numbers()
    {
        var result = Read(Header + "\r\nC1,Acme,a@example.com,INV-1,2026-04-01,2026-05-01,100.50\r\n,\"Beta, Ltd\",,INV-2,01/04/2026,01/05/2026,\"1,25,000\"\r\n");

        Assert.True(result.IsValid);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(2, result.Rows[0].RowNumber);
        Assert.Equal("Beta, Ltd", result.Rows[1].CustomerName);
        Assert.Null(result.Rows[1].CustomerCode);
        Assert.Equal("1,25,000", result.Rows[1].InvoiceAmount);
    }

    [Fact]
    public void Utf8_bom_and_header_case_are_accepted()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(Header.ToLowerInvariant() + "\nC1,Acme,,INV-1,2026-04-01,2026-05-01,10\n")).ToArray();

        var result = InvoiceCsvReader.Read(bytes, "x.CSV", Limits);

        Assert.True(result.IsValid);
        Assert.Single(result.Rows);
    }

    [Fact]
    public void Empty_file_is_rejected()
    {
        Assert.Contains("empty", Read("").FileErrors.Single());
        Assert.Contains("empty", Read("\r\n\r\n").FileErrors.Single());
    }

    [Fact]
    public void Header_only_file_is_rejected()
    {
        Assert.Contains("no data rows", Read(Header + "\r\n").FileErrors.Single());
    }

    [Fact]
    public void Missing_required_headers_are_reported()
    {
        var result = Read("CustomerName,InvoiceNumber,InvoiceAmount\nAcme,INV-1,10\n");

        var error = result.FileErrors.Single();
        Assert.Contains("InvoiceDate", error);
        Assert.Contains("DueDate", error);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public void Non_csv_extension_is_rejected_regardless_of_content()
    {
        Assert.Contains(".csv", Read(Header + "\n", "invoices.xlsx").FileErrors.Single());
        Assert.Contains(".csv", Read(Header + "\n", "invoices.csv.exe").FileErrors.Single());
    }

    [Fact]
    public void Oversized_file_is_rejected()
    {
        var result = Read(Header + "\n" + new string('x', 20_000));

        Assert.Contains("larger than", result.FileErrors.Single());
    }

    [Fact]
    public void Too_many_rows_is_rejected()
    {
        var sb = new StringBuilder(Header + "\n");
        for (var i = 0; i < 4; i++)
        {
            sb.AppendLine($",Acme,,INV-{i},2026-04-01,2026-05-01,10");
        }

        Assert.Contains("limit is 3", Read(sb.ToString()).FileErrors.Single());
    }

    [Fact]
    public void Invalid_utf8_and_binary_content_are_rejected()
    {
        var latin1 = new byte[] { 0x41, 0x2C, 0xE9, 0x0A };
        Assert.Contains("UTF-8", InvoiceCsvReader.Read(latin1, "a.csv", Limits).FileErrors.Single());

        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(Header)).ToArray();
        Assert.Contains("UTF-16", InvoiceCsvReader.Read(utf16, "a.csv", Limits).FileErrors.Single());

        var zip = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0x00 };
        Assert.Contains("binary", InvoiceCsvReader.Read(zip, "a.csv", Limits).FileErrors.Single());
    }

    [Fact]
    public void Unclosed_quote_is_reported()
    {
        Assert.Contains("not closed", Read(Header + "\n,\"Acme,,INV-1,2026-04-01,2026-05-01,10\n").FileErrors.Single());
    }

    [Fact]
    public void Payment_columns_are_ignored_with_explicit_notice()
    {
        var result = Read(Header + ",AmountPaid\n,Acme,,INV-1,2026-04-01,2026-05-01,10,5\n");

        Assert.True(result.IsValid);
        Assert.Contains(result.Notices, n => n.Contains("AmountPaid") && n.Contains("Payments are not imported"));
    }

    [Fact]
    public void Template_is_itself_a_valid_import_file()
    {
        var result = InvoiceCsvReader.Read(Encoding.UTF8.GetBytes(InvoiceCsvReader.BuildTemplate()), "template.csv", new ImportLimits());

        Assert.True(result.IsValid);
        Assert.Equal(2, result.Rows.Count);
    }
}
