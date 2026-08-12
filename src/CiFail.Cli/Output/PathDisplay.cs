namespace CiFail.Cli.Output;

/// <summary>
/// Shortens a source label (a log path, or a <c>file::TestName</c> from an expanded test
/// report) so it fits the width it is rendered in.
///
/// <para>
/// Truncation goes from the <b>left</b>, because the end of a path is the part that identifies
/// it — the drive and the home directory are the part nobody needs. Spectre's own truncation
/// works the other way and drops a long path entirely (its <see cref="Spectre.Console.Rule"/>
/// title is word-wrapped and only the first line survives, so an absolute path — one word with
/// no spaces to wrap at — reduces to a bare ellipsis). CI systems pass absolute paths, so that
/// is the common case, not the rare one: every report in a job that analyzes several logs
/// looked identical.
/// </para>
/// </summary>
public static class PathDisplay
{
    private static readonly char[] Separators = { '/', '\\' };

    /// <summary>
    /// The label shortened to at most <paramref name="maxWidth"/> characters, keeping the tail.
    /// Returns the label unchanged when it already fits.
    /// </summary>
    public static string Elide(string label, int maxWidth)
    {
        if (maxWidth <= 0) return string.Empty;
        if (string.IsNullOrEmpty(label) || label.Length <= maxWidth) return label;

        var ellipsis = Glyphs.Ellipsis;

        // Too narrow to spend characters on the ellipsis: the tail alone says more.
        if (maxWidth <= ellipsis.Length) return label[^maxWidth..];

        var keep = maxWidth - ellipsis.Length;
        var start = label.Length - keep;

        // Prefer cutting at a separator so what remains reads as a path rather than a fragment
        // of a directory name — but only when the boundary is close, since moving the cut
        // forward costs real characters.
        var boundary = label.IndexOfAny(Separators, start);
        if (boundary >= 0 && boundary - start <= keep / 4) start = boundary;

        return ellipsis + label[start..];
    }
}
