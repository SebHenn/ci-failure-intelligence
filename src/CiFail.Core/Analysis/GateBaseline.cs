using System.Text;

namespace CiFail.Core.Analysis;

/// <summary>
/// Reading and writing the gate's baseline file — the list of failures that are already
/// known and therefore must not fail the build.
///
/// <para>
/// The format is one fingerprint per line with <c>#</c> comments, rather than JSON or YAML,
/// for one reason: this file is <b>committed and reviewed</b>. A reviewer should be able to
/// see "one line added, and it says what it is" in a diff, and delete a line by hand to
/// re-arm the gate for that failure.
/// </para>
/// </summary>
public static class GateBaseline
{
    /// <summary>Directory the baseline lives in, relative to the repo root.</summary>
    public const string DirectoryName = ".cifail";

    /// <summary>File name of the baseline.</summary>
    public const string FileName = "baseline.txt";

    /// <summary>Default location: <c>.cifail/baseline.txt</c>, relative to the working directory.</summary>
    public static string DefaultPath => Path.Combine(DirectoryName, FileName);

    /// <summary>
    /// Parse a baseline file's contents into the set of accepted fingerprints. Unparseable
    /// junk is skipped rather than throwing: a baseline is an accept-list, so the failure
    /// mode of a bad line must be "that failure is not accepted" (the gate fires), never a
    /// crash that takes the build down for an unrelated reason.
    /// </summary>
    public static IReadOnlySet<string> Parse(string? content)
    {
        var accepted = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(content)) return accepted;

        foreach (var raw in content.Split('\n'))
        {
            var line = raw;

            // Strip the trailing comment that Render writes (and anything a human added).
            var hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];

            // A fingerprint never contains whitespace, so the first token is the whole of it.
            var token = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrEmpty(token))
                accepted.Add(token);
        }

        return accepted;
    }

    /// <summary>
    /// Render a baseline file. Entries are sorted so that regenerating it produces a stable
    /// diff — an unsorted file would churn on every run and make review worthless.
    /// </summary>
    public static string Render(IEnumerable<GateFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);

        var entries = findings
            .GroupBy(f => f.Fingerprint, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(f => f.Fingerprint, StringComparer.Ordinal)
            .ToList();

        var sb = new StringBuilder();
        sb.Append(
            """
            # cifail baseline — the failures that are already known and accepted.
            #
            # `cifail gate` fails only on a fingerprint that is NOT listed here, so a newly
            # introduced failure breaks the build while the existing backlog does not.
            #
            # Delete a line to re-arm the gate for that failure. Regenerate the whole file with:
            #     cifail gate --update <logs>
            #
            # Commit this file: it is the gate's entire memory, and it has to be the same for
            # everyone. Text after '#' is a comment and is ignored.

            """);

        if (entries.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("# (no accepted failures)");
            return sb.ToString();
        }

        sb.AppendLine();
        var width = entries.Max(e => e.Fingerprint.Length);
        foreach (var entry in entries)
        {
            var label = Describe(entry);
            sb.Append(entry.Fingerprint.PadRight(width));
            if (label is not null) sb.Append("  # ").Append(label);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// The human-readable half of a baseline line. Collapsed to a single line and clipped,
    /// because a rule title with a newline in it would silently corrupt the file format.
    /// </summary>
    private static string? Describe(GateFinding finding)
    {
        var text = string.IsNullOrWhiteSpace(finding.Title) ? finding.RuleId : finding.Title;
        if (string.IsNullOrWhiteSpace(text)) return null;

        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        flat = flat.Replace('#', '-');
        return flat.Length <= 70 ? flat : flat[..69] + "…";
    }
}
