using System.Text;
using System.Text.RegularExpressions;

namespace Collections.Core.Reminders;

public static class ReminderTemplateDefaults
{
    public const string TemplateKey = "invoice-standard";

    public const string Subject = "Payment reminder for invoice {{InvoiceNumber}}";

    public const string Body =
        "Hello {{CustomerName}},\n\n" +
        "Our records show an outstanding balance of {{OutstandingAmount}} for invoice {{InvoiceNumber}}, due on {{DueDate}}.\n\n" +
        "If payment has already been made, please disregard this reminder and share the payment reference so we can update our records.\n\n" +
        "For any discrepancy, please contact {{BusinessName}} at {{BusinessContact}}.\n\n" +
        "Regards,\n" +
        "{{BusinessName}}";
}

/// <summary>
/// Plain-text template rendering with a fixed set of {{Placeholder}} variables.
/// No code execution, no nested expansion: a value containing "{{...}}" is inserted literally.
/// </summary>
public static partial class ReminderTemplateRenderer
{
    public const int SubjectMaxLength = 200;
    public const int BodyMaxLength = 4000;

    public static readonly IReadOnlyList<string> Placeholders = new[]
    {
        "CustomerName", "InvoiceNumber", "InvoiceDate", "DueDate", "InvoiceAmount", "OutstandingAmount", "BusinessName", "BusinessContact",
    };

    [GeneratedRegex(@"\{\{\s*([A-Za-z]+)\s*\}\}")]
    private static partial Regex PlaceholderRegex();

    /// <summary>Returns problems with a template (unknown placeholders, length), or an empty list.</summary>
    public static IReadOnlyList<string> Validate(string? template, int maxLength)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(template))
        {
            errors.Add("Template is required.");
            return errors;
        }

        if (template.Length > maxLength)
        {
            errors.Add($"Template must be at most {maxLength} characters.");
        }

        foreach (Match match in PlaceholderRegex().Matches(template))
        {
            var name = match.Groups[1].Value;
            if (!Placeholders.Contains(name, StringComparer.Ordinal))
            {
                errors.Add($"Unknown placeholder {{{{{name}}}}}. Allowed: {string.Join(", ", Placeholders.Select(p => "{{" + p + "}}"))}.");
            }
        }

        return errors;
    }

    public static string Render(string template, IReadOnlyDictionary<string, string> values)
        => PlaceholderRegex().Replace(template, m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);

    /// <summary>Renders a subject line: single line, no control characters (prevents header injection).</summary>
    public static string RenderSubject(string template, IReadOnlyDictionary<string, string> values)
    {
        var rendered = Render(template, values);
        var sb = new StringBuilder(rendered.Length);
        foreach (var ch in rendered)
        {
            sb.Append(char.IsControl(ch) ? ' ' : ch);
        }

        var subject = sb.ToString().Trim();
        return subject.Length > SubjectMaxLength ? subject[..SubjectMaxLength] : subject;
    }
}
