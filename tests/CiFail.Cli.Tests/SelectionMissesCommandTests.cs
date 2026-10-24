using System.Text.Json;
using FluentAssertions;

namespace CiFail.Cli.Tests;

/// <summary>
/// <c>cifail selection-misses</c>: which failing tests dotnet-tia's selection had skipped.
/// </summary>
[Collection(CliCollection.Name)]
public sealed class SelectionMissesCommandTests
{
    private const string TiaReport = """
        {
          "schemaVersion": 1,
          "mode": "selective",
          "baseRef": "origin/main",
          "headCommit": "0123456789abcdef",
          "skippedTests": [ { "project": "App.Tests", "tests": ["App.Tests.GridTests.Wraps"] } ]
        }
        """;

    private const string JUnit = """
        <testsuites>
          <testsuite name="App.Tests">
            <testcase classname="App.Tests.GridTests" name="Wraps"><failure message="expected 3, got 4">trace</failure></testcase>
            <testcase classname="App.Tests.GridTests" name="Draws"><failure message="broken">trace</failure></testcase>
            <testcase classname="App.Tests.GridTests" name="Fills" />
          </testsuite>
        </testsuites>
        """;

    private static (string Tia, string Results) Write(CliHarness cli, string tia, string results)
    {
        var tiaPath = Path.Combine(cli.Home, "tia-report.json");
        var resultsPath = Path.Combine(cli.Home, "results.xml");
        File.WriteAllText(tiaPath, tia);
        File.WriteAllText(resultsPath, results);
        return (tiaPath, resultsPath);
    }

    [Fact]
    public void A_failure_of_a_skipped_test_is_named_and_exits_one()
    {
        using var cli = new CliHarness();
        var (tia, results) = Write(cli, TiaReport, JUnit);

        var result = cli.Run("selection-misses", "--tia", tia, results);

        result.ExitCode.Should().Be(ExitCodes.Negative);
        // Separately: in CI the console is coloured, and the commit is in bold.
        result.StdoutFlat.Should().Contain("1 of 2 failing test(s) were skipped by dotnet-tia at");
        result.StdoutFlat.Should().Contain("012345678");
        result.StdoutFlat.Should().Contain("App.Tests.GridTests.Wraps");
        result.StdoutFlat.Should().NotContain("GridTests.Draws", "that test ran, so its failure is not a selection miss");
    }

    [Fact]
    public void Json_lists_the_misses()
    {
        using var cli = new CliHarness();
        var (tia, results) = Write(cli, TiaReport, JUnit);

        var result = cli.Run("selection-misses", "--tia", tia, results, "--json");

        result.ExitCode.Should().Be(ExitCodes.Negative);
        using var json = JsonDocument.Parse(result.Stdout);
        json.RootElement.GetProperty("failingTests").GetInt32().Should().Be(2);
        var miss = json.RootElement.GetProperty("misses").EnumerateArray().Single();
        miss.GetProperty("test").GetString().Should().Be("App.Tests.GridTests.Wraps");
        miss.GetProperty("project").GetString().Should().Be("App.Tests");
    }

    [Fact]
    public void No_skipped_test_failed_exits_zero()
    {
        using var cli = new CliHarness();
        var (tia, results) = Write(cli, TiaReport.Replace("GridTests.Wraps", "GridTests.Fills", StringComparison.Ordinal), JUnit);

        var result = cli.Run("selection-misses", "--tia", tia, results);

        result.ExitCode.Should().Be(ExitCodes.Ok);
        result.StdoutFlat.Should().Contain("No selection miss");
    }

    [Fact]
    public void A_report_without_skipped_tests_says_how_to_write_one()
    {
        using var cli = new CliHarness();
        var (tia, results) = Write(cli, """{ "schemaVersion": 1, "mode": "selective" }""", JUnit);

        var result = cli.Run("selection-misses", "--tia", tia, results);

        result.ExitCode.Should().Be(ExitCodes.Usage);
        result.StderrFlat.Should().Contain("--report-skipped");
    }

    [Fact]
    public void Only_logs_and_no_report_is_refused_rather_than_read_as_no_misses()
    {
        using var cli = new CliHarness();
        var (tia, _) = Write(cli, TiaReport, JUnit);
        var log = Path.Combine(cli.Home, "build.log");
        File.WriteAllText(log, "error CS1002: ; expected");

        var result = cli.Run("selection-misses", "--tia", tia, log);

        result.ExitCode.Should().Be(ExitCodes.Usage);
        result.StderrFlat.Should().Contain("nothing to check");
    }

    [Fact]
    public void A_log_beside_the_reports_is_passed_over_with_a_warning()
    {
        // What a results directory looks like: the runner's log next to its TRX.
        using var cli = new CliHarness();
        var (tia, results) = Write(cli, TiaReport, JUnit);
        File.WriteAllText(Path.Combine(cli.Home, "run.log"), "Test run finished");

        var result = cli.Run("selection-misses", "--tia", tia, results, Path.Combine(cli.Home, "run.log"));

        result.ExitCode.Should().Be(ExitCodes.Negative);
        result.StderrFlat.Should().Contain("is not a JUnit or TRX report; skipped");
        result.StdoutFlat.Should().Contain("App.Tests.GridTests.Wraps");
    }

    [Fact]
    public void The_tia_report_is_required()
    {
        using var cli = new CliHarness();
        var (_, results) = Write(cli, TiaReport, JUnit);

        cli.Run("selection-misses", results).ExitCode.Should().Be(ExitCodes.Usage);
    }
}
