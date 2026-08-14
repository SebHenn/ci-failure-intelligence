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
    /// <summary>
    /// Largest log cifail will analyze in full, in characters (~8 MB of text).
    ///
    /// <para>
    /// Nothing bounded this. A <see cref="LogDocument"/> holds the raw text, the normalized text
    /// and a line array — three full copies — and <see cref="Scrub"/> makes seven more passes over
    /// the whole thing on the similarity path, so a 200 MB log meant gigabytes of allocation, all
    /// of it on the large object heap. Past roughly a billion characters .NET cannot represent the
    /// string at all and the read throws before analysis even starts.
    /// </para>
    /// </summary>
    public const int MaxCharacters = 8 * 1024 * 1024;

    /// <summary>
    /// Marker inserted where the middle of an oversized log was dropped. Deliberately visible: a
    /// silently truncated log is one where the user cannot tell why a rule didn't fire.
    /// </summary>
    public const string ElisionMarker = "[... cifail: middle of the log omitted ...]";

    public static LogDocument Build(string source, string rawText)
    {
        // RawText is the windowed text, not the caller's original: the document describes what was
        // actually analyzed, and holding a reference to a hundreds-of-megabytes string we chose
        // not to look at would defeat the point of windowing it.
        var windowed = Window(rawText);
        var normalized = Normalize(windowed);
        var lines = normalized.Split('\n');
        return new LogDocument
        {
            Source = source,
            RawText = windowed,
            NormalizedText = normalized,
            NormalizedLines = lines,
        };
    }

    /// <summary>
    /// Keep the head and the tail of an oversized log, dropping the middle.
    ///
    /// <para>
    /// Not a hard refusal, and not a plain <c>Take(n)</c>: in CI the failure is almost always near
    /// the <i>end</i> (the compiler error, the assertion, the exit code), while the beginning
    /// carries the tool versions and the command line that identify the ecosystem. Cutting from
    /// the front would throw away the answer; cutting from the back would throw away the context
    /// that detection needs. So both ends are kept and the middle — usually thousands of lines of
    /// progress output — goes.
    /// </para>
    /// </summary>
    public static string Window(string raw, int maxCharacters = MaxCharacters)
    {
        if (string.IsNullOrEmpty(raw) || raw.Length <= maxCharacters) return raw;

        // Weighted to the tail, which is where the failure is.
        var headBudget = maxCharacters / 4;
        var tailBudget = maxCharacters - headBudget - ElisionMarker.Length - 2;

        // Snap to line boundaries so neither half starts or ends mid-line.
        var headEnd = raw.LastIndexOf('\n', Math.Min(headBudget, raw.Length - 1));
        if (headEnd <= 0) headEnd = headBudget;

        var tailStart = raw.IndexOf('\n', raw.Length - tailBudget);
        if (tailStart < 0 || tailStart >= raw.Length - 1) tailStart = raw.Length - tailBudget;

        return new StringBuilder(maxCharacters)
            .Append(raw, 0, headEnd)
            .Append('\n').Append(ElisionMarker).Append('\n')
            .Append(raw, tailStart + 1, raw.Length - tailStart - 1)
            .ToString();
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
