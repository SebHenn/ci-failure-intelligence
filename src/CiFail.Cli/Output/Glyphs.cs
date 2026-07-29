using System.Text;

namespace CiFail.Cli.Output;

/// <summary>
/// The non-ASCII characters used in console output, with an ASCII fallback for terminals
/// that can't encode them.
///
/// <para>
/// cifail leans on a handful of glyphs (✓ ✗ ⚠ • … — × ·) to keep the report scannable. On a
/// legacy Windows console still running a DOS code page (437/850) those encode to <c>?</c>,
/// so a passing run reads "? 12 rules". <see cref="CliApp"/> asks for UTF-8 first; when the
/// console refuses, these degrade to something honest instead.
/// </para>
///
/// <para>
/// Only console output uses these. Files and payloads that cifail writes as UTF-8 by
/// construction — SARIF, Markdown reports, notification bodies — keep their real characters.
/// </para>
/// </summary>
public static class Glyphs
{
    /// <summary>True when the console encoding can actually represent these characters.</summary>
    public static bool Unicode { get; } = Detect();

    public static string Check => Unicode ? "✓" : "OK";
    public static string Cross => Unicode ? "✗" : "X";
    public static string Warning => Unicode ? "⚠" : "!";
    public static string Bullet => Unicode ? "•" : "*";
    public static string Ellipsis => Unicode ? "…" : "...";
    public static string Dash => Unicode ? "—" : "-";
    public static string Times => Unicode ? "×" : "x";
    public static string Dot => Unicode ? "·" : "|";

    /// <summary>
    /// Probe the console encoding by round-tripping the glyphs through it. A code page that
    /// can't represent them maps every one to <c>?</c>, so the round-trip is the direct test —
    /// more reliable than allow-listing code pages.
    /// </summary>
    private static bool Detect()
    {
        try
        {
            var encoding = Console.OutputEncoding;
            if (encoding.CodePage is 65001 or 1200 or 1201) return true;

            const string probe = "✓✗⚠•…—×·";
            return encoding.GetString(encoding.GetBytes(probe)) == probe;
        }
        catch (Exception ex) when (ex is IOException or EncoderFallbackException or DecoderFallbackException)
        {
            // No console attached (or a hostile encoding): assume ASCII, which always renders.
            return false;
        }
    }
}
