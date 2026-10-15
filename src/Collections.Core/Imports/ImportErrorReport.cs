using System.Text;
using Collections.Core.Entities;

namespace Collections.Core.Imports;

/// <summary>Builds a downloadable CSV of rejected rows. All values are formula-escaped.</summary>
public static class ImportErrorReport
{
    public static string Build(IEnumerable<ImportRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CsvWriter.Line(new[] { "RowNumber" }.Concat(InvoiceCsvReader.Columns).Append("Errors")));

        foreach (var row in rows.Where(r => r.PlannedAction == ImportRowAction.Error || r.Outcome == ImportRowOutcome.Rejected).OrderBy(r => r.RowNumber))
        {
            sb.AppendLine(CsvWriter.Line(new[]
            {
                row.RowNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                row.CustomerCode,
                row.CustomerName,
                row.CustomerEmail,
                row.InvoiceNumber,
                row.InvoiceDate,
                row.DueDate,
                row.InvoiceAmount,
                row.Errors?.Replace("\n", " | "),
            }));
        }

        return sb.ToString();
    }
}
