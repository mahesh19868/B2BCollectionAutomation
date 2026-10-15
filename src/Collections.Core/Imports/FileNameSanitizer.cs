using System.Text;

namespace Collections.Core.Imports;

public static class FileNameSanitizer
{
    public const int MaxLength = 100;

    /// <summary>
    /// Reduces a client-supplied file name to a safe display name: strips any path,
    /// keeps letters, digits, space, dot, dash and underscore, and limits length.
    /// </summary>
    public static string Sanitize(string? fileName)
    {
        var name = fileName ?? string.Empty;
        var lastSeparator = name.LastIndexOfAny(new[] { '/', '\\' });
        if (lastSeparator >= 0)
        {
            name = name[(lastSeparator + 1)..];
        }

        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is ' ' or '.' or '-' or '_')
            {
                builder.Append(ch);
            }
            else
            {
                builder.Append('_');
            }
        }

        var cleaned = builder.ToString().Trim(' ', '.');
        while (cleaned.Contains(".."))
        {
            cleaned = cleaned.Replace("..", ".");
        }

        if (cleaned.Length > MaxLength)
        {
            cleaned = cleaned[^MaxLength..];
        }

        return string.IsNullOrWhiteSpace(cleaned) ? "upload.csv" : cleaned;
    }
}
