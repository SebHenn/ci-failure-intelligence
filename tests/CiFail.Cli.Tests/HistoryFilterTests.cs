using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace CiFail.Cli.Tests;

/// <summary>
/// `cifail history` filters and `--json` (R39).
///
/// <para>
/// history was the one data-bearing command with no machine-readable output and no filters —
/// only <c>--limit</c> — even though every stored record is fully structured and <c>stats</c> and
/// <c>clusters</c> already accepted <c>--since</c>/<c>--repo</c>. That made the history unusable
/// from a script.
/// </para>
/// </summary>
[Collection(CliCollection.Name)]
public class HistoryFilterTests
{
    private static void Seed(CliHarness cli)
    {
        cli.Run("analyze", "--no-git", TestPaths.Sample("nuget-nu1101.log"));
        cli.Run("analyze", "--no-git", TestPaths.Sample("node-eresolve.log"));
        cli.Run("resolve", "1", "--note", "fixed the feed");
    }

    private static JsonElement Json(CliHarness cli, params string[] args)
    {
        var result = cli.Run(args);
        result.ExitCode.Should().Be(ExitCodes.Ok);
        return JsonDocument.Parse(result.Stdout).RootElement;
    }

    [Fact]
    public void Json_lists_every_record()
    {
        using var cli = new CliHarness();
        Seed(cli);

        var rows = Json(cli, "history", "--json");

        rows.ValueKind.Should().Be(JsonValueKind.Array);
        rows.GetArrayLength().Should().Be(2);
    }

    [Fact]
    public void Newest_comes_first()
    {
        using var cli = new CliHarness();
        Seed(cli);

        var rows = Json(cli, "history", "--json");

        rows[0].GetProperty("Id").GetInt64().Should().BeGreaterThan(
            rows[1].GetProperty("Id").GetInt64());
    }

    [Fact]
    public void Resolved_and_open_select_opposite_halves()
    {
        using var cli = new CliHarness();
        Seed(cli);

        var resolved = Json(cli, "history", "--resolved", "--json");
        var open = Json(cli, "history", "--open", "--json");

        resolved.GetArrayLength().Should().Be(1);
        resolved[0].GetProperty("RuleId").GetString().Should().Be("nuget-nu1101");

        open.GetArrayLength().Should().Be(1);
        open[0].GetProperty("RuleId").GetString().Should().Be("npm-eresolve");
    }

    [Fact]
    public void Ecosystem_and_rule_filter()
    {
        using var cli = new CliHarness();
        Seed(cli);

        Json(cli, "history", "--ecosystem", "node", "--json").GetArrayLength().Should().Be(1);
        Json(cli, "history", "--rule", "nuget-nu1101", "--json").GetArrayLength().Should().Be(1);
        Json(cli, "history", "--ecosystem", "rust", "--json").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public void Search_matches_the_source()
    {
        using var cli = new CliHarness();
        Seed(cli);

        Json(cli, "history", "--search", "eresolve", "--json").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public void Limit_and_offset_page()
    {
        using var cli = new CliHarness();
        Seed(cli);

        var first = Json(cli, "history", "--limit", "1", "--json");
        var second = Json(cli, "history", "--limit", "1", "--offset", "1", "--json");

        first.GetArrayLength().Should().Be(1);
        second.GetArrayLength().Should().Be(1);
        first[0].GetProperty("Id").GetInt64()
            .Should().NotBe(second[0].GetProperty("Id").GetInt64());
    }

    [Fact]
    public void A_single_record_serializes_as_an_object()
    {
        using var cli = new CliHarness();
        Seed(cli);

        var record = Json(cli, "history", "1", "--json");

        record.ValueKind.Should().Be(JsonValueKind.Object);
        record.GetProperty("Id").GetInt64().Should().Be(1);
        record.GetProperty("Resolution").GetString().Should().Be("fixed the feed");
    }

    [Fact]
    public void Contradictory_status_filters_are_a_usage_error()
    {
        using var cli = new CliHarness();

        var result = cli.Run("history", "--open", "--resolved");

        result.ExitCode.Should().Be(ExitCodes.Usage);
        result.StderrFlat.Should().Contain("mutually exclusive");
    }

    /// <summary>An unreadable date must be reported, not silently ignored as "no filter".</summary>
    [Fact]
    public void An_unparseable_since_is_a_usage_error()
    {
        using var cli = new CliHarness();

        var result = cli.Run("history", "--since", "last tuesday");

        result.ExitCode.Should().Be(ExitCodes.Usage);
        result.StderrFlat.Should().Contain("could not read the date");
    }

    [Fact]
    public void An_empty_filtered_result_says_so_on_stderr_and_keeps_stdout_clean()
    {
        using var cli = new CliHarness();
        Seed(cli);

        var result = cli.Run("history", "--ecosystem", "rust");

        result.ExitCode.Should().Be(ExitCodes.Ok);
        result.StderrFlat.Should().Contain("No analyses match those filters");
        result.Stdout.Trim().Should().BeEmpty();
    }
}
