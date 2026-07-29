using FluentAssertions;

namespace CiFail.Cli.Tests;

/// <summary>
/// Smoke coverage for every registered command's help.
///
/// <para>
/// Cheap but load-bearing: <c>--help</c> is the first thing a new user runs, and it's also the
/// only thing that exercises a command's settings class end to end. A duplicated short flag, a
/// malformed <c>[CommandOption]</c> template, or an example that doesn't parse are all
/// construction-time faults that no other test would catch — and they'd surface as a stack
/// trace on the user's very first command.
/// </para>
/// </summary>
[Collection(CliCollection.Name)]
public sealed class HelpTests
{
    /// <summary>
    /// Every command path the CLI registers. Kept explicit rather than reflected: the point is
    /// to assert the command is *reachable by the name a user types*, which reflection over the
    /// command classes would not prove.
    /// </summary>
    public static TheoryData<string[]> Commands => new()
    {
        new[] { "analyze" },
        new[] { "history" },
        new[] { "stats" },
        new[] { "clusters" },
        new[] { "resolve" },
        new[] { "suggest-rule" },
        new[] { "reconcile" },
        new[] { "init" },
        new[] { "config" },
        new[] { "doctor" },
        new[] { "rules" },
        new[] { "rules", "list" },
        new[] { "rules", "test" },
        new[] { "rules", "validate" },
        new[] { "rules", "explain" },
    };

    [Theory]
    [MemberData(nameof(Commands))]
    public void Help_succeeds_and_says_nothing_on_stderr(string[] command)
    {
        using var cli = new CliHarness();
        var result = cli.Run(command.Append("--help").ToArray());

        result.ExitCode.Should().Be(ExitCodes.Ok);
        result.Stdout.Should().NotBeEmpty();
        result.Stderr.Should().BeEmpty("help is the answer to what was asked, so nothing is a diagnostic");
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public void Help_describes_the_command(string[] command)
    {
        using var cli = new CliHarness();
        var result = cli.Run(command.Append("--help").ToArray());

        // Spectre renders USAGE from the settings class; its absence means the command was
        // registered but its settings failed to bind.
        result.StdoutFlat.Should().Contain("USAGE");
        result.StdoutFlat.Should().Contain("cifail");
    }

    [Fact]
    public void Root_help_lists_every_top_level_command()
    {
        using var cli = new CliHarness();
        var result = cli.Run("--help");

        result.ExitCode.Should().Be(ExitCodes.Ok);
        foreach (var name in new[] { "analyze", "history", "stats", "clusters", "resolve", "reconcile", "init", "config", "rules" })
            result.StdoutFlat.Should().Contain(name);
    }

    [Fact]
    public void Every_command_reports_a_version()
    {
        using var cli = new CliHarness();
        var result = cli.Run("--version");

        result.StdoutFlat.Should().Be(CliApp.Version);
        result.Stderr.Should().BeEmpty();
    }

    [Fact]
    public void The_type_option_advertises_the_ecosystems_that_actually_exist()
    {
        using var cli = new CliHarness();
        var help = cli.Run("analyze", "--help").StdoutFlat;

        // The help list used to be hand-written and had drifted four ecosystems behind. It now
        // comes from EcosystemDetector.SupportedNamesText, and this proves the wiring.
        foreach (var name in Core.Ingest.EcosystemDetector.SupportedNames)
            help.Should().Contain(name);
    }
}
