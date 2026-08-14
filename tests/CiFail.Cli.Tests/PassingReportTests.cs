using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace CiFail.Cli.Tests;

/// <summary>
/// A structured test report that parsed cleanly and contains no failures.
///
/// <para>
/// This path returned early, before any output was produced, so every machine-readable format
/// silently emitted nothing on exactly the runs where nothing was wrong: <c>--json</c> wrote an
/// empty stream (so a downstream <c>jq</c> failed on it), and <c>--report sarif --report-out</c>
/// never created the file — which breaks the <c>upload-sarif</c> chain the README documents,
/// because the upload step then fails on a missing artifact.
/// </para>
/// </summary>
[Collection(CliCollection.Name)]
public class PassingReportTests
{
    private const string PassingJUnit = """
        <?xml version="1.0" encoding="UTF-8"?>
        <testsuites>
          <testsuite name="green" tests="2" failures="0" errors="0">
            <testcase classname="Suite" name="passes" />
            <testcase classname="Suite" name="also_passes" />
          </testsuite>
        </testsuites>
        """;

    private static string WriteReport(CliHarness cli)
    {
        var path = Path.Combine(cli.Home, "passing.xml");
        File.WriteAllText(path, PassingJUnit);
        return path;
    }

    [Fact]
    public void Json_emits_an_empty_array_rather_than_nothing()
    {
        using var cli = new CliHarness();
        var result = cli.Run("analyze", "--no-git", "--json", "--format", "junit", WriteReport(cli));

        result.ExitCode.Should().Be(0);

        var document = JsonDocument.Parse(result.Stdout);
        document.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        document.RootElement.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public void Sarif_report_file_is_still_written()
    {
        using var cli = new CliHarness();
        var output = Path.Combine(cli.Home, "reports", "cifail.sarif");

        var result = cli.Run("analyze", "--no-git", "--format", "junit",
            "--report", "sarif", "--report-out", output, WriteReport(cli));

        result.ExitCode.Should().Be(0);
        File.Exists(output).Should().BeTrue("upload-sarif fails on a missing file");

        var document = JsonDocument.Parse(File.ReadAllText(output));
        document.RootElement.GetProperty("version").GetString().Should().Be("2.1.0");
        document.RootElement.GetProperty("runs")[0].GetProperty("results")
            .GetArrayLength().Should().Be(0);
    }

    /// <summary>--report-out into a directory that doesn't exist yet must create it, as gate does.</summary>
    [Fact]
    public void Report_out_creates_its_parent_directory()
    {
        using var cli = new CliHarness();
        var output = Path.Combine(cli.Home, "nested", "deeper", "cifail.md");

        cli.Run("analyze", "--no-git", "--report", "markdown", "--report-out", output,
            TestPaths.Sample("nuget-nu1101.log"));

        File.Exists(output).Should().BeTrue();
    }

    [Fact]
    public void The_human_view_still_says_nothing_failed()
    {
        using var cli = new CliHarness();
        var result = cli.Run("analyze", "--no-git", "--format", "junit", WriteReport(cli));

        result.ExitCode.Should().Be(0);
        result.Stdout.Should().Contain("No failing tests found");
    }
}
