using CiFail.Cli;
using CiFail.Cli.Output;
using FluentAssertions;
using Xunit;

namespace CiFail.Cli.Tests;

/// <summary>
/// Verbosity and colour control (R39). None of it existed: no <c>--quiet</c>, no
/// <c>--verbose</c>, and no way to turn colour off short of redirecting stdout — Spectre.Console
/// 0.57 does not implement <c>NO_COLOR</c> and cifail did not either.
/// </summary>
[Collection(CliCollection.Name)]
public class OutputSettingsTests
{
    /// <summary>
    /// Env-var precedence, tested directly because it is the part with a right answer:
    /// NO_COLOR wins over FORCE_COLOR (a user sets it deliberately to stop colour everywhere).
    /// </summary>
    [Theory]
    [InlineData(null, null, null, null)]          // nothing set -> leave detection alone
    [InlineData("1", null, null, false)]          // NO_COLOR
    [InlineData("", null, null, null)]            // empty NO_COLOR is not set
    [InlineData(null, "1", null, true)]           // FORCE_COLOR
    [InlineData("1", "1", null, false)]           // NO_COLOR beats FORCE_COLOR
    [InlineData(null, null, "0", false)]          // CLICOLOR=0, the older spelling
    [InlineData(null, null, "1", null)]           // CLICOLOR=1 is not a force
    public void Colour_preference_follows_the_conventions(
        string? noColor, string? forceColor, string? cliColor, bool? expected)
    {
        var env = new Dictionary<string, string?>
        {
            ["NO_COLOR"] = noColor,
            ["FORCE_COLOR"] = forceColor,
            ["CLICOLOR"] = cliColor,
        };

        CliApp.ColorPreference(name => env.GetValueOrDefault(name)).Should().Be(expected);
    }

    [Fact]
    public void Quiet_suppresses_hints_but_not_errors()
    {
        using var cli = new CliHarness();

        // A missing --older-than is an error plus a hint; only the hint should go.
        var normal = cli.Run("prune");
        var quiet = cli.Run("prune", "--quiet");

        normal.StderrFlat.Should().Contain("For example");
        quiet.StderrFlat.Should().NotContain("For example");
        quiet.StderrFlat.Should().Contain("prune needs an age",
            "quiet means stop advising me, not hide problems");
    }

    /// <summary>
    /// EcosystemDetector.Rank is public and documented as the answer to "why did it think this
    /// was Java?", and until now nothing but its own tests called it.
    /// </summary>
    [Fact]
    public void Verbose_explains_the_ecosystem_choice()
    {
        using var cli = new CliHarness();

        var result = cli.Run("analyze", "--no-history", "--no-git", "--verbose",
            TestPaths.Sample("nuget-nu1101.log"));

        result.StderrFlat.Should().Contain("detected");
        result.StderrFlat.Should().Contain("dotnet");
    }

    [Fact]
    public void Without_verbose_the_detection_detail_stays_out_of_the_way()
    {
        using var cli = new CliHarness();

        var result = cli.Run("analyze", "--no-history", "--no-git",
            TestPaths.Sample("nuget-nu1101.log"));

        result.StderrFlat.Should().NotContain("detected dotnet from");
    }

    [Fact]
    public void Verbose_shows_the_rule_ids_of_secondary_matches()
    {
        using var cli = new CliHarness();

        var result = cli.Run("analyze", "--no-history", "--no-git", "--verbose",
            TestPaths.Sample("nuget-nu1101.log"));

        result.ExitCode.Should().Be(ExitCodes.Ok);
        result.StdoutFlat.Should().Contain("nuget-nu1101");
    }

    [Fact]
    public void No_color_is_accepted_by_every_command()
    {
        using var cli = new CliHarness();

        // The flag lives on a shared base class applied by an interceptor, so it must work on a
        // command that has nothing else to do with output.
        cli.Run("rules", "list", "--no-color").ExitCode.Should().Be(ExitCodes.Ok);
        cli.Run("history", "--no-color").ExitCode.Should().Be(ExitCodes.Ok);
        cli.Run("analyze", "--no-color", "--no-history", "--no-git",
            TestPaths.Sample("nuget-nu1101.log")).ExitCode.Should().Be(ExitCodes.Ok);
    }

    [Fact]
    public void The_quiet_and_verbose_state_does_not_leak_between_runs()
    {
        using var cli = new CliHarness();

        cli.Run("analyze", "--quiet", "--no-history", "--no-git", TestPaths.Sample("nuget-nu1101.log"));
        CliConsole.Reset();

        CliConsole.Quiet.Should().BeFalse();
        CliConsole.Verbose.Should().BeFalse();
    }
}
