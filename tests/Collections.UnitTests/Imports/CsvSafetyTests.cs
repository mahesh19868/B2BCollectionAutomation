using Collections.Core.Entities;
using Collections.Core.Imports;

namespace Collections.UnitTests.Imports;

public class CsvSafetyTests
{
    [Theory]
    [InlineData("=HYPERLINK(\"http://x\")", "\"'=HYPERLINK(\"\"http://x\"\")\"")]
    [InlineData("+1+1", "\"'+1+1\"")]
    [InlineData("-2", "\"'-2\"")]
    [InlineData("@SUM(A1)", "\"'@SUM(A1)\"")]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData(null, "")]
    public void Csv_values_are_formula_escaped_and_quoted(string? input, string expected)
    {
        Assert.Equal(expected, CsvWriter.Escape(input));
    }

    [Fact]
    public void Error_report_contains_only_rejected_rows_and_escapes_values()
    {
        var rows = new[]
        {
            new ImportRow { RowNumber = 3, CustomerName = "=cmd|' /C calc'!A0", InvoiceNumber = "INV-1", PlannedAction = ImportRowAction.Error, Errors = "InvoiceAmount: bad\nDueDate: bad" },
            new ImportRow { RowNumber = 2, CustomerName = "Fine", InvoiceNumber = "INV-2", PlannedAction = ImportRowAction.Create },
        };

        var report = ImportErrorReport.Build(rows);
        var lines = report.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length);
        Assert.StartsWith("RowNumber,CustomerCode", lines[0]);
        Assert.Contains("\"'=cmd|' /C calc'!A0\"", lines[1]);
        Assert.Contains("InvoiceAmount: bad | DueDate: bad", lines[1]);
        Assert.DoesNotContain("INV-2", report);
    }

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("C:\\Users\\x\\invoices.csv", "invoices.csv")]
    [InlineData("<script>alert(1)<\\script>.csv", "script_.csv")]
    [InlineData("in<v>oi\"ce|s*?.csv", "in_v_oi_ce_s__.csv")]
    [InlineData("..csv", "csv")]
    [InlineData("", "upload.csv")]
    [InlineData(null, "upload.csv")]
    public void Malicious_file_names_are_sanitised(string? input, string expected)
    {
        Assert.Equal(expected, FileNameSanitizer.Sanitize(input));
    }

    [Fact]
    public void Long_file_names_are_truncated()
    {
        Assert.Equal(FileNameSanitizer.MaxLength, FileNameSanitizer.Sanitize(new string('a', 500) + ".csv").Length);
    }

    [Fact]
    public void Csv_parser_handles_quotes_escaped_quotes_and_embedded_newlines()
    {
        var result = CsvParser.Parse("a,\"b \"\"q\"\"\",\"line1\nline2\"\r\nx,y,z");

        Assert.Null(result.Error);
        Assert.Equal(2, result.Records.Count);
        Assert.Equal(new[] { "a", "b \"q\"", "line1\nline2" }, result.Records[0].Fields);
        Assert.Equal(2, result.Records[1].RowNumber);
    }
}
