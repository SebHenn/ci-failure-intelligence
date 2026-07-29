using System.Globalization;
using System.Text;
using CiFail.Cli;
using CiFail.Cli.Output;
using CiFail.Core;
using Spectre.Console;

namespace CiFail.Cli.Tests;

/// <summary>The two streams and the exit code of one <c>cifail</c> invocation.</summary>
/// <param name="ExitCode">What the process would have returned.</param>
/// <param name="Stdout">The answer stream.</param>
/// <param name="Stderr">The diagnostics stream.</param>
public sealed record CliResult(int ExitCode, string Stdout, string Stderr)
{
    /// <summary>Stdout with line breaks and padding collapsed, for substring assertions.</summary>
    public string StdoutFlat => Flatten(Stdout);

    /// <summary>Stderr with line breaks and padding collapsed, for substring assertions.</summary>
    public string StderrFlat => Flatten(Stderr);

    /// <summary>
    /// Spectre wraps and pads inside panels and tables, so a message can be split mid-sentence
    /// across lines. Collapsing whitespace lets a test assert on the message, not the layout.
    /// </summary>
    private static string Flatten(string s) =>
        string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>
/// Runs the real CLI in-process: same <see cref="CliApp.Build"/> the entry point uses, with
/// both output streams captured separately.
///
/// <para>
/// Capturing <b>both</b> streams is the point. Most of what this suite guards is the rule that
/// stdout carries the answer and stderr carries everything else — you cannot test that with a
/// single combined buffer, which is exactly why the bug (warnings breaking
/// <c>analyze --json | jq</c>) survived so long.
/// </para>
///
/// <para>
/// Console redirection, <c>AnsiConsole.Console</c> and <c>CIFAIL_HOME</c> are all process-global,
/// so every test using this harness must run serially — see <see cref="CliCollection"/>.
/// </para>
/// </summary>
public sealed class CliHarness : IDisposable
{
    private readonly string? _previousHome;

    public CliHarness()
    {
        Home = Path.Combine(Path.GetTempPath(), "cifail-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Home);

        // Without this the tests would write history into the developer's real ~/.cifail.
        _previousHome = Environment.GetEnvironmentVariable(CiFailPaths.HomeEnvVar);
        Environment.SetEnvironmentVariable(CiFailPaths.HomeEnvVar, Home);
    }

    /// <summary>An isolated CIFAIL_HOME for this test.</summary>
    public string Home { get; }

    /// <summary>Path to <c>config.yaml</c> inside this harness's home (may not exist).</summary>
    public string ConfigPath => Path.Combine(Home, "config.yaml");

    /// <summary>Write a <c>config.yaml</c> for the run to pick up.</summary>
    public void WriteConfig(string yaml) => File.WriteAllText(ConfigPath, yaml);

    /// <summary>Run cifail with the given arguments and capture both streams.</summary>
    public CliResult Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var originalOut = Console.Out;
        var originalError = Console.Error;
        var originalAnsi = AnsiConsole.Console;
        var originalUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            // Spectre.Console.Cli localizes USAGE/OPTIONS/EXAMPLES from satellite assemblies, so
            // help assertions would pass in CI and fail on a developer machine with a non-English
            // locale. Pinning the UI culture asserts the help users actually get: the shipped
            // binary sets SatelliteResourceLanguages=en, so it is English-only by construction.
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

            // Commands write JSON/SARIF straight to Console.Out, and annotations to
            // Console.Error, so the raw streams must be captured as well as the Spectre ones.
            Console.SetOut(stdout);
            Console.SetError(stderr);

            var outConsole = CreateConsole(stdout);
            AnsiConsole.Console = outConsole;
            CliConsole.Out = outConsole;
            CliConsole.Err = CreateConsole(stderr);

            var exitCode = CliApp.Build(outConsole).Run(args);
            return new CliResult(exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            CliConsole.Reset();
            AnsiConsole.Console = originalAnsi;
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    /// <summary>
    /// A plain-text Spectre console over a writer: no colour, no ANSI, and a wide enough profile
    /// that table cells aren't truncated into unassertable fragments.
    /// </summary>
    private static IAnsiConsole CreateConsole(TextWriter writer)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(writer),
        });
        console.Profile.Width = 200;
        console.Profile.Encoding = Encoding.UTF8;
        return console;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(CiFailPaths.HomeEnvVar, _previousHome);
        try
        {
            if (Directory.Exists(Home)) Directory.Delete(Home, recursive: true);
        }
        catch (IOException)
        {
            // A held file handle must not fail an otherwise passing test.
        }
    }
}

/// <summary>
/// Serializes every CLI test. The harness mutates process-global state (console redirection,
/// <c>AnsiConsole.Console</c>, environment variables), so parallel tests would see each other's
/// output.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CliCollection
{
    public const string Name = "cli";
}
