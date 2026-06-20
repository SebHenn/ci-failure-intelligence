using System.Text;
using System.Text.RegularExpressions;
using CiFail.Core.Models;

namespace CiFail.Core.Ingest;

/// <summary>
/// Turns raw log text into a <see cref="LogDocument"/>: strips ANSI escape codes,
/// normalizes line endings, and removes leading CI timestamps so rule matching and
/// similarity are stable across runs. Also exposes <see cref="Scrub"/> for building
/// volatile-noise-free signatures used by fingerprints and similarity.
/// </summary>
public static partial class LogNormalizer
{
    public static LogDocument Build(string source, string rawText)
    {
        var normalized = Normalize(rawText);
        var lines = normalized.Split('\n');
        return new LogDocument
        {
            Source = source,
            RawText = rawText,
            NormalizedText = normalized,
            NormalizedLines = lines,
        };
    }

    /// <summary>Strip ANSI, normalize newlines, drop leading CI timestamps, trim trailing space.</summary>
    public static string Normalize(string raw)
    {
        var text = StripAnsi(raw).Replace("\r\n", "\n").Replace('\r', '\n');

        var sb = new StringBuilder(text.Length);
        foreach (var line in text.Split('\n'))
        {
            var cleaned = LeadingTimestamp().Replace(line, string.Empty);
            sb.Append(cleaned.TrimEnd()).Append('\n');
        }

        // Trim trailing newlines (including any from a final blank line).
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>Remove ANSI/VT100 escape sequences.</summary>
    public static string StripAnsi(string text) => AnsiEscape().Replace(text, string.Empty);

    /// <summary>
    /// Collapse volatile tokens (paths, GUIDs, hex addresses, numbers) to placeholders
    /// and lowercase, so logically identical failures produce the same signature.
    /// </summary>
    public static string Scrub(string text)
    {
        text = StripAnsi(text);
        text = WindowsPath().Replace(text, "<path>");
        text = UnixPath().Replace(text, "<path>");
        text = Guid().Replace(text, "<guid>");
        text = HexAddress().Replace(text, "<hex>");
        text = Number().Replace(text, "<n>");
        return text.ToLowerInvariant().Trim();
    }

    [GeneratedRegex(@"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])")]
    private static partial Regex AnsiEscape();

    // ISO-ish timestamps or [hh:mm:ss] prefixes some CI systems prepend to each line.
    [GeneratedRegex(@"^\s*(?:\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:[.,]\d+)?(?:Z|[+-]\d{2}:?\d{2})?|\[\d{2}:\d{2}:\d{2}(?:[.,]\d+)?\])\s*")]
    private static partial Regex LeadingTimestamp();

    [GeneratedRegex(@"[A-Za-z]:\\(?:[^\\/:*?""<>|\r\n]+\\?)+")]
    private static partial Regex WindowsPath();

    [GeneratedRegex(@"(?<![\w-])/(?:[^/\s:]+/)+[^/\s:]*")]
    private static partial Regex UnixPath();

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")]
    private static partial Regex Guid();

    [GeneratedRegex(@"\b0x[0-9a-fA-F]+\b")]
    private static partial Regex HexAddress();

    [GeneratedRegex(@"\b\d+\b")]
    private static partial Regex Number();
}
