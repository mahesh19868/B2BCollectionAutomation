using System.Text;

namespace Collections.Core.Imports;

public sealed record CsvRecord(int RowNumber, IReadOnlyList<string> Fields);

public sealed record CsvParseResult(IReadOnlyList<CsvRecord> Records, string? Error);

/// <summary>
/// Minimal RFC 4180 CSV parser: comma separated, double-quote quoting with "" escapes,
/// CRLF or LF line endings, and line breaks inside quoted fields.
/// Row numbers count records (header = 1), matching what a spreadsheet shows.
/// </summary>
public static class CsvParser
{
    public static CsvParseResult Parse(string text, int maxRecords = int.MaxValue)
    {
        var records = new List<CsvRecord>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var fieldStarted = false;
        var rowNumber = 1;
        var i = 0;

        void EndField()
        {
            fields.Add(field.ToString());
            field.Clear();
            fieldStarted = false;
        }

        bool EndRecord()
        {
            EndField();
            records.Add(new CsvRecord(rowNumber, fields.ToArray()));
            fields.Clear();
            rowNumber++;
            return records.Count <= maxRecords;
        }

        while (i < text.Length)
        {
            var c = text[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i += 2;
                        continue;
                    }

                    inQuotes = false;
                    i++;
                    continue;
                }

                field.Append(c);
                i++;
                continue;
            }

            switch (c)
            {
                case '"' when !fieldStarted && field.Length == 0:
                    inQuotes = true;
                    fieldStarted = true;
                    i++;
                    break;
                case '"':
                    return new CsvParseResult(records, $"Row {rowNumber}: unexpected quote character inside an unquoted value.");
                case ',':
                    EndField();
                    i++;
                    break;
                case '\r':
                case '\n':
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        i++;
                    }

                    i++;
                    if (!EndRecord())
                    {
                        return new CsvParseResult(records, null);
                    }

                    break;
                default:
                    field.Append(c);
                    fieldStarted = true;
                    i++;
                    break;
            }
        }

        if (inQuotes)
        {
            return new CsvParseResult(records, $"Row {rowNumber}: a quoted value is not closed.");
        }

        // Final record without trailing newline.
        if (field.Length > 0 || fields.Count > 0 || fieldStarted)
        {
            EndRecord();
        }

        return new CsvParseResult(records, null);
    }
}

/// <summary>Writes CSV values safely, neutralising spreadsheet formula injection.</summary>
public static class CsvWriter
{
    private static readonly char[] FormulaPrefixes = { '=', '+', '-', '@', '\t', '\r' };

    public static string Escape(string? value)
    {
        value ??= string.Empty;

        if (value.Length > 0 && Array.IndexOf(FormulaPrefixes, value[0]) >= 0)
        {
            value = "'" + value;
        }

        var needsQuotes = value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 || value.StartsWith('\'');
        return needsQuotes ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    public static string Line(IEnumerable<string?> values) => string.Join(",", values.Select(Escape));
}
