using Spectre.Console;

namespace CiFail.Cli.Output;

/// <summary>
/// The CLI's two output streams, and the one rule that governs them:
/// <b>stdout carries the answer, stderr carries everything about the run.</b>
///
/// <para>
/// That distinction is what makes cifail scriptable. Before this existed every message went
/// to stdout, so a single warning would land in the middle of <c>cifail analyze --json | jq</c>
/// and break the pipe. Tables, <c>--json</c>, SARIF and GitHub annotations are the answer and
/// stay on <see cref="Out"/>; errors, warnings and hints describe the run and go to
/// <see cref="Err"/>.
/// </para>
///
/// <para>
/// Two deliberate exceptions, because there the diagnostics <i>are</i> the answer:
/// <c>rules validate</c>'s lint output and <c>rules test</c>'s "no match." both stay on stdout.
/// </para>
/// </summary>
public static class CliConsole
{
    private static IAnsiConsole? _out;
    private static IAnsiConsole? _err;

    /// <summary>
    /// The answer stream. Defaults to <see cref="AnsiConsole.Console"/> rather than caching it,
    /// so a test that swaps the global console is picked up here too.
    /// </summary>
    public static IAnsiConsole Out
    {
        get => _out ?? AnsiConsole.Console;
        set => _out = value;
    }

    /// <summary>The diagnostics stream: a second Spectre console writing to <see cref="Console.Error"/>.</summary>
    public static IAnsiConsole Err
    {
        get => _err ??= AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(Console.Error),
        });
        set => _err = value;
    }

    /// <summary>Point both streams back at the process defaults (used by tests).</summary>
    public static void Reset()
    {
        _out = null;
        _err = null;
    }

    /// <summary>
    /// Write <c>error: &lt;markup&gt;</c> to stderr. The argument is Spectre markup, matching
    /// the surrounding call sites — escape interpolated values with <see cref="Markup.Escape"/>.
    /// </summary>
    public static void Error(string markup) => Err.MarkupLine($"[red]error:[/] {markup}");

    /// <summary>Write <c>warning: &lt;markup&gt;</c> to stderr. Same markup contract as <see cref="Error"/>.</summary>
    public static void Warn(string markup) => Err.MarkupLine($"[yellow]warning:[/] {markup}");

    /// <summary>
    /// Write an untagged advisory line to stderr — the "here's what to do instead" that follows
    /// an error, and the "nothing to show yet" notes that would otherwise pollute a piped stdout.
    /// </summary>
    public static void Hint(string markup) => Err.MarkupLine(markup);

    /// <summary>Set to <c>1</c> to see the stack trace behind an error instead of just the message.</summary>
    public const string DebugEnvVar = "CIFAIL_DEBUG";

    /// <summary>True when the user asked for stack traces.</summary>
    public static bool IsDebug =>
        Environment.GetEnvironmentVariable(DebugEnvVar) is "1" or "true" or "TRUE";

    /// <summary>
    /// Dump <paramref name="ex"/> to stderr when <c>CIFAIL_DEBUG=1</c>, otherwise do nothing.
    ///
    /// <para>
    /// Call this from <b>every</b> place that swallows an exception to print a friendly message.
    /// The debug switch promises the stack trace behind any error, and a handler that reports
    /// nicely without offering this makes that promise a lie precisely where it's needed.
    /// </para>
    /// </summary>
    public static void DumpIfDebug(Exception ex)
    {
        if (IsDebug) Err.WriteException(ex, ExceptionFormats.ShortenPaths);
    }
}
