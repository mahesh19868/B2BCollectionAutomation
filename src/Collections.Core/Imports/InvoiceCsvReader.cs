using System.Text;

namespace Collections.Core.Imports;

public sealed class ImportLimits
{
    public long MaxFileBytes { get; set; } = 2 * 1024 * 1024;
    public int MaxRows { get; set; } = 5000;
}

/// <summary>Raw text values of one data row, exactly as uploaded (trimmed).</summary>
public sealed record InvoiceImportRawRow(
    int RowNumber,
    string? CustomerCode,
    string? CustomerName,
    string? CustomerEmail,
    string? InvoiceNumber,
    string? InvoiceDate,
    string? DueDate,
    string? InvoiceAmount);

public sealed record InvoiceFileReadResult(
    IReadOnlyList<string> FileErrors,
    IReadOnlyList<string> Notices,
    IReadOnlyList<InvoiceImportRawRow> Rows)
{
    public bool IsValid => FileErrors.Count == 0;
}

/// <summary>Invoice CSV template definition and file-level validation (type, size, encoding, headers, row count).</summary>
public static class InvoiceCsvReader
{
    public const string CustomerCode = "CustomerCode";
    public const string CustomerName = "CustomerName";
    public const string CustomerEmail = "CustomerEmail";
    public const string InvoiceNumber = "InvoiceNumber";
    public const string InvoiceDate = "InvoiceDate";
    public const string DueDate = "DueDate";
    public const string InvoiceAmount = "InvoiceAmount";

    public static readonly string[] Columns = { CustomerCode, CustomerName, CustomerEmail, InvoiceNumber, InvoiceDate, DueDate, InvoiceAmount };
    public static readonly string[] RequiredColumns = { CustomerName, InvoiceNumber, InvoiceDate, DueDate, InvoiceAmount };

    private static readonly string[] PaymentLikeColumns = { "PaymentAmount", "AmountPaid", "PaidAmount", "Paid", "Received", "Balance", "Outstanding" };

    public static string BuildTemplate()
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", Columns));
        sb.AppendLine("C001,Sample Traders Pvt Ltd,accounts@example.com,INV-2026-0001,2026-04-01,2026-05-01,125000.00");
        sb.AppendLine(",Example Retail,,INV-2026-0002,15/04/2026,15/05/2026,\"1,25,000.00\"");
        return sb.ToString();
    }

    public static InvoiceFileReadResult Read(byte[] content, string fileName, ImportLimits limits)
    {
        var errors = new List<string>();
        var notices = new List<string>();

        if (!fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        {
            return Fail("Only .csv files are supported. In Excel use File > Save As > CSV UTF-8 (Comma delimited).");
        }

        if (content.Length == 0)
        {
            return Fail("The file is empty.");
        }

        if (content.Length > limits.MaxFileBytes)
        {
            return Fail($"The file is larger than the {limits.MaxFileBytes / 1024} KB limit.");
        }

        var decoded = DecodeUtf8(content);
        if (decoded.Error is not null)
        {
            return Fail(decoded.Error);
        }

        var parsed = CsvParser.Parse(decoded.Text!);
        if (parsed.Error is not null)
        {
            return Fail($"The file is not valid CSV. {parsed.Error}");
        }

        var nonBlank = parsed.Records.Where(r => r.Fields.Any(f => !string.IsNullOrWhiteSpace(f))).ToList();
        if (nonBlank.Count == 0)
        {
            return Fail("The file is empty.");
        }

        var header = nonBlank[0];
        if (header.RowNumber != 1)
        {
            return Fail("The first row must contain the column headers.");
        }

        var headerNames = header.Fields.Select(h => h.Trim()).ToList();
        var columnIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < headerNames.Count; i++)
        {
            var name = headerNames[i];
            if (name.Length == 0)
            {
                continue;
            }

            if (!columnIndex.TryAdd(name, i))
            {
                errors.Add($"Column '{name}' appears more than once in the header row.");
            }
        }

        var missing = RequiredColumns.Where(c => !columnIndex.ContainsKey(c)).ToList();
        if (missing.Count > 0)
        {
            errors.Add($"Missing required column(s): {string.Join(", ", missing)}. Download the template to see the expected headers.");
        }

        if (errors.Count > 0)
        {
            return new InvoiceFileReadResult(errors, notices, Array.Empty<InvoiceImportRawRow>());
        }

        foreach (var unknown in columnIndex.Keys.Where(k => !Columns.Contains(k, StringComparer.OrdinalIgnoreCase)))
        {
            notices.Add(PaymentLikeColumns.Contains(unknown, StringComparer.OrdinalIgnoreCase)
                ? $"Column '{unknown}' is ignored. Payments are not imported from this file; record payments on the Payments page."
                : $"Column '{unknown}' is not recognised and is ignored.");
        }

        var dataRows = nonBlank.Skip(1).ToList();
        var blankCount = parsed.Records.Count - nonBlank.Count;
        if (blankCount > 0)
        {
            notices.Add($"{blankCount} completely blank row(s) were ignored.");
        }

        if (dataRows.Count == 0)
        {
            return Fail("The file has a header row but no data rows.");
        }

        if (dataRows.Count > limits.MaxRows)
        {
            return Fail($"The file has {dataRows.Count} data rows; the limit is {limits.MaxRows}. Split the file and import each part.");
        }

        string? Get(CsvRecord record, string column)
        {
            if (!columnIndex.TryGetValue(column, out var index) || index >= record.Fields.Count)
            {
                return null;
            }

            var value = record.Fields[index].Trim();
            return value.Length == 0 ? null : value;
        }

        var rows = dataRows.Select(r => new InvoiceImportRawRow(
                r.RowNumber,
                Get(r, CustomerCode),
                Get(r, CustomerName),
                Get(r, CustomerEmail),
                Get(r, InvoiceNumber),
                Get(r, InvoiceDate),
                Get(r, DueDate),
                Get(r, InvoiceAmount)))
            .ToList();

        return new InvoiceFileReadResult(errors, notices, rows);

        InvoiceFileReadResult Fail(string message)
            => new(new[] { message }, notices, Array.Empty<InvoiceImportRawRow>());
    }

    private static (string? Text, string? Error) DecodeUtf8(byte[] content)
    {
        if ((content.Length >= 2 && content[0] == 0xFF && content[1] == 0xFE) ||
            (content.Length >= 2 && content[0] == 0xFE && content[1] == 0xFF))
        {
            return (null, "The file is UTF-16 encoded. Save it as 'CSV UTF-8 (Comma delimited)' and upload again.");
        }

        var offset = content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF ? 3 : 0;

        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(content, offset, content.Length - offset);
        }
        catch (DecoderFallbackException)
        {
            return (null, "The file is not valid UTF-8 text. Save it as 'CSV UTF-8 (Comma delimited)' and upload again.");
        }

        if (text.Contains('\0'))
        {
            return (null, "The file appears to be binary (for example an .xlsx renamed to .csv). Save it as CSV and upload again.");
        }

        return (text, null);
    }
}
