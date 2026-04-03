using System.Text;
using System.Text.RegularExpressions;

namespace LilAgents.Windows.Sessions;

internal static partial class SessionOutputSanitizer
{
    private const int DefaultMaxLength = 600;
    private const string TruncationSuffix = "...";

    [GeneratedRegex(@"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])", RegexOptions.Compiled)]
    private static partial Regex AnsiEscapeRegex();

    [GeneratedRegex(@"\x1B\][^\x07]*(?:\x07|\x1B\\)", RegexOptions.Compiled)]
    private static partial Regex OscEscapeRegex();

    public static bool TrySanitizeLine(string? rawLine, out string sanitizedLine, int maxLength = DefaultMaxLength)
    {
        sanitizedLine = string.Empty;
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return false;
        }

        var noOsc = OscEscapeRegex().Replace(rawLine, string.Empty);
        var noAnsi = AnsiEscapeRegex().Replace(noOsc, string.Empty).Replace("\u001b", string.Empty);

        var builder = new StringBuilder(noAnsi.Length);
        foreach (var ch in noAnsi)
        {
            if (ch == '\r' || ch == '\n' || ch == '\t' || !char.IsControl(ch))
            {
                builder.Append(ch);
            }
        }

        var collapsed = Regex.Replace(builder.ToString(), @"\s{2,}", " ").Trim();
        if (string.IsNullOrWhiteSpace(collapsed))
        {
            return false;
        }

        if (collapsed.Length > maxLength)
        {
            collapsed = collapsed[..maxLength] + TruncationSuffix;
        }

        sanitizedLine = collapsed;
        return true;
    }

    public static bool IsLikelyHtmlNoise(string line)
    {
        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith('<'))
        {
            return false;
        }

        return trimmed.Contains("<html", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("<head", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("<body", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("<meta", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("<script", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("<style", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("</", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsLikelyStackTrace(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("at ", StringComparison.Ordinal) ||
               trimmed.StartsWith("at async ", StringComparison.Ordinal) ||
               trimmed.StartsWith("at Object.", StringComparison.Ordinal) ||
               trimmed.Contains(".js:", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("node:internal/", StringComparison.OrdinalIgnoreCase);
    }
}
