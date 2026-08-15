using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace CiFail.Cli.Tests;

/// <summary>
/// `cifail rules list --json` / `rules explain --json` (R40) — the rule inventory was not
/// scriptable, so nothing could enumerate what a given cifail build actually knows.
/// </summary>
[Collection(CliCollection.Name)]
public class RulesJsonTests
{
    private static JsonElement Json(CliHarness cli, params string[] args)
    {
        var result = cli.Run(args);
        result.ExitCode.Should().Be(ExitCodes.Ok);
        return JsonDocument.Parse(result.Stdout).RootElement;
    }

    [Fact]
    public void Rules_list_json_carries_every_shipped_rule()
    {
        using var cli = new CliHarness();

        var doc = Json(cli, "rules", "list", "--json");

        doc.GetProperty("Count").GetInt32().Should().BeGreaterThan(100);
        doc.GetProperty("Rules").GetArrayLength().Should().Be(doc.GetProperty("Count").GetInt32());
    }

    /// <summary>
    /// "Which rules do I have" and "where did they come from" are the same question once a repo
    /// can ship its own packs, so the search path travels with the inventory.
    /// </summary>
    [Fact]
    public void Rules_list_json_includes_the_search_path()
    {
        using var cli = new CliHarness();

        var doc = Json(cli, "rules", "list", "--json");

        doc.GetProperty("SearchPath").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public void Each_rule_carries_the_fields_a_script_would_want()
    {
        using var cli = new CliHarness();

        var rule = Json(cli, "rules", "list", "--json").GetProperty("Rules")
            .EnumerateArray().First(r => r.GetProperty("Id").GetString() == "nuget-nu1101");

        rule.GetProperty("Ecosystems")[0].GetString().Should().Be("dotnet");
        rule.GetProperty("Category").GetString().Should().Be("dependency");
        rule.GetProperty("Confidence").GetDouble().Should().BeGreaterThan(0);
        rule.GetProperty("Match").GetString().Should().NotBeEmpty();
        rule.GetProperty("Docs").GetString().Should().StartWith("http");
    }

    /// <summary>Optional R36 fields are omitted when unset, so "not declared" is distinguishable.</summary>
    [Fact]
    public void Unset_optional_fields_are_omitted()
    {
        using var cli = new CliHarness();

        var rule = Json(cli, "rules", "list", "--json").GetProperty("Rules")
            .EnumerateArray().First(r => r.GetProperty("Id").GetString() == "nuget-nu1101");

        rule.TryGetProperty("Severity", out _).Should().BeFalse();
        rule.TryGetProperty("NotMatch", out _).Should().BeFalse();
        rule.TryGetProperty("Enabled", out _).Should().BeFalse("an enabled rule is the norm");
    }

    [Fact]
    public void Rules_explain_json_is_a_single_object_naming_its_source()
    {
        using var cli = new CliHarness();

        var rule = Json(cli, "rules", "explain", "nuget-nu1101", "--json");

        rule.ValueKind.Should().Be(JsonValueKind.Object);
        rule.GetProperty("Id").GetString().Should().Be("nuget-nu1101");
        rule.GetProperty("Source").GetString().Should().Be("embedded default");
    }

    [Fact]
    public void Rules_explain_json_for_an_unknown_id_still_exits_not_found()
    {
        using var cli = new CliHarness();

        var result = cli.Run("rules", "explain", "no-such-rule", "--json");

        result.ExitCode.Should().Be(ExitCodes.NotFound);
    }
}
