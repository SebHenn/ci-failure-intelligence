using System.ComponentModel;
using CiFail.Cli.Output;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CiFail.Cli.Commands;

/// <summary>
/// Options every command shares for controlling how much it says and whether it says it in
/// colour.
///
/// <para>
/// None of these existed: there was no <c>--quiet</c>, no <c>--verbose</c>, and no way at all to
/// turn colour off short of redirecting stdout — which also loses the human view. Spectre.Console
/// 0.57 does not implement <c>NO_COLOR</c> and cifail did not either.
/// </para>
/// </summary>
public class OutputSettings : CommandSettings
{
    [CommandOption("--no-color")]
    [Description("Disable coloured output (also honours the NO_COLOR environment variable).")]
    public bool NoColor { get; init; }

    [CommandOption("-q|--quiet")]
    [Description("Suppress hints and progress notes; errors and the result itself still print.")]
    public bool Quiet { get; init; }

    [CommandOption("-v|--verbose")]
    [Description("Show extra detail: every secondary match, and why the ecosystem was chosen.")]
    public bool Verbose { get; init; }

    /// <summary>
    /// Push the flags into the process-wide console state. Called by each command before it
    /// renders anything.
    ///
    /// <para>
    /// Spectre.Console.Cli has no notion of a global option, so a shared base class plus an
    /// explicit call is the honest way to do this — the alternative is repeating three flags on
    /// every settings class and forgetting one.
    /// </para>
    /// </summary>
    public void Apply()
    {
        if (NoColor)
        {
            AnsiConsole.Console.Profile.Capabilities.Ansi = false;
            AnsiConsole.Console.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;
        }

        CliConsole.Quiet = Quiet;
        CliConsole.Verbose = Verbose;
    }
}

/// <summary>
/// Applies <see cref="OutputSettings"/> before any command runs. Registered once via
/// <c>config.SetInterceptor</c>, so a new command inherits the behaviour by deriving its settings
/// from <see cref="OutputSettings"/> and nothing else.
/// </summary>
public sealed class OutputSettingsInterceptor : ICommandInterceptor
{
    public void Intercept(CommandContext context, CommandSettings settings)
    {
        if (settings is OutputSettings output) output.Apply();
    }
}
