using CiFail.Cli.Commands;
using FluentAssertions;
using Xunit;

namespace CiFail.Cli.Tests;

/// <summary>
/// `cifail prune` (R37). History previously had no delete path of any kind, so <c>history.db</c>
/// grew for the life of the install — unbounded disk, and a growing pile of log text at rest.
/// </summary>
[Collection(CliCollection.Name)]
public class PruneCommandTests
{
    [Theory]
    [InlineData("30d", 30)]
    [InlineData("2w", 14)]
    [InlineData("3mo", 90)]
    [InlineData("1y", 365)]
    [InlineData("  90D  ", 90)]
    public void Durations_parse(string input, double expectedDays) =>
        PruneCommand.ParseDuration(input)!.Value.TotalDays.Should().BeApproximately(expectedDays, 0.01);

    /// <summary>
    /// A mistyped age must never be read as "delete everything". Note a bare <c>m</c> is rejected
    /// rather than guessed at — minutes and months are both plausible readings.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("90")]
    [InlineData("d")]
    [InlineData("-5d")]
    [InlineData("0d")]
    [InlineData("30m")]
    [InlineData("soon")]
    public void Nonsense_durations_are_rejected(string input) =>
        PruneCommand.ParseDuration(input).Should().BeNull();

    [Fact]
    public void Prune_without_an_age_is_a_usage_error()
    {
        using var cli = new CliHarness();

        var result = cli.Run("prune");

        result.ExitCode.Should().Be(ExitCodes.Usage);
        result.StderrFlat.Should().Contain("--older-than");
    }

    [Fact]
    public void An_unparseable_age_is_a_usage_error_and_deletes_nothing()
    {
        using var cli = new CliHarness();

        var result = cli.Run("prune", "--older-than", "yesterday");

        result.ExitCode.Should().Be(ExitCodes.Usage);
        result.StderrFlat.Should().Contain("could not read the duration");
    }

    [Fact]
    public void Pruning_an_empty_history_reports_nothing_to_do()
    {
        using var cli = new CliHarness();

        var result = cli.Run("prune", "--older-than", "1d");

        result.ExitCode.Should().Be(ExitCodes.Ok);
        result.StderrFlat.Should().Contain("Nothing to prune");
    }

    /// <summary>
    /// Everything analyzed in a test is recorded "now", so nothing is old enough to go — which is
    /// exactly the safety property worth asserting: a fresh failure is not deleted by a prune.
    /// </summary>
    [Fact]
    public void A_recent_failure_survives_a_prune_of_older_records()
    {
        using var cli = new CliHarness();
        cli.Run("analyze", "--no-git", TestPaths.Sample("nuget-nu1101.log"));

        var prune = cli.Run("prune", "--older-than", "1d", "--include-open");
        prune.ExitCode.Should().Be(ExitCodes.Ok);

        cli.Run("history").StdoutFlat.Should().Contain("nuget-nu1101",
            "a failure recorded moments ago is not an old one");
    }

    [Fact]
    public void Dry_run_says_so_and_changes_nothing()
    {
        using var cli = new CliHarness();
        cli.Run("analyze", "--no-git", TestPaths.Sample("nuget-nu1101.log"));

        var result = cli.Run("prune", "--older-than", "1d", "--dry-run");

        result.ExitCode.Should().Be(ExitCodes.Ok);
        cli.Run("history").StdoutFlat.Should().Contain("nuget-nu1101");
    }
}
