using System.Text.Json;
using CiFail.Cli;
using FluentAssertions;
using Xunit;

namespace CiFail.Cli.Tests;

/// <summary>
/// <c>--report</c> and <c>--report-out</c> given more than once.
///
/// <para>
/// They were single-valued, so a second pair silently replaced the first: exit 0, empty stderr,
/// and a file the caller explicitly asked for that was never written. The shipped GitHub Action
/// builds exactly that command line — <c>--report markdown --report-out .cifail-report.md</c>
/// plus <c>--report sarif --report-out $INPUT_SARIF</c> — so whenever <c>sarif:</c> was supplied
/// the markdown report it then renders the job summary and PR comment from did not exist.
/// A markdown report for humans and a SARIF for Code Scanning is the obvious pairing, and it
/// should cost one analysis, not two.
/// </para>
/// </summary>
[Collection(CliCollection.Name)]
public class MultipleReportsTests
{
    private static string Log() => TestPaths.Sample("nuget-nu1101.log");

    [Fact]
    public void Both_pairs_are_written_from_one_run()
    {
        using var cli = new CliHarness();
        var markdown = Path.Combine(cli.Home, ".cifail-report.md");
        var sarif = Path.Combine(cli.Home, "cifail.sarif");

        var result = cli.Run("analyze", "--no-git", "--json", Log(),
            "--report", "markdown", "--report-out", markdown,
            "--report", "sarif", "--report-out", sarif);

        result.ExitCode.Should().Be(ExitCodes.Ok);
        File.Exists(markdown).Should().BeTrue("the first pair used to be dropped in silence");
        File.Exists(sarif).Should().BeTrue();

        File.ReadAllText(markdown).Should().Contain("cifail");
        JsonDocument.Parse(File.ReadAllText(sarif))
            .RootElement.GetProperty("version").GetString().Should().Be("2.1.0");
    }

    /// <summary>Symmetric: whichever pair was last used to be the only one that existed.</summary>
    [Fact]
    public void The_order_of_the_pairs_does_not_matter()
    {
        using var cli = new CliHarness();
        var markdown = Path.Combine(cli.Home, "report.md");
        var sarif = Path.Combine(cli.Home, "report.sarif");

        cli.Run("analyze", "--no-git", "--json", Log(),
            "--report", "sarif", "--report-out", sarif,
            "--report", "markdown", "--report-out", markdown);

        File.Exists(sarif).Should().BeTrue();
        File.Exists(markdown).Should().BeTrue();
    }

    /// <summary>With every report going to a file, --json still owns stdout.</summary>
    [Fact]
    public void Json_still_owns_stdout_when_both_reports_go_to_files()
    {
        using var cli = new CliHarness();

        var result = cli.Run("analyze", "--no-git", "--json", Log(),
            "--report", "markdown", "--report-out", Path.Combine(cli.Home, "a.md"),
            "--report", "sarif", "--report-out", Path.Combine(cli.Home, "a.sarif"));

        var document = JsonDocument.Parse(result.Stdout);
        document.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        document.RootElement.GetArrayLength().Should().Be(1);
    }

    /// <summary>A single report with no destination keeps taking over stdout.</summary>
    [Fact]
    public void One_report_without_a_destination_still_goes_to_stdout()
    {
        using var cli = new CliHarness();
        var result = cli.Run("analyze", "--no-git", "--json", Log(), "--report", "markdown");

        result.ExitCode.Should().Be(ExitCodes.Ok);
        result.Stdout.Should().StartWith("## cifail analysis",
            "the report takes over stdout, suppressing the --json view");
    }

    /// <summary>
    /// Spectre hands each option over as its own list, so the interleaving is lost and a
    /// lopsided pairing has two equally plausible readings. Refuse rather than guess — silently
    /// picking one is the bug this whole class is about.
    /// </summary>
    [Fact]
    public void A_report_without_its_own_destination_is_a_usage_error_once_there_are_several()
    {
        using var cli = new CliHarness();

        var result = cli.Run("analyze", "--no-git", Log(),
            "--report", "markdown", "--report", "sarif",
            "--report-out", Path.Combine(cli.Home, "only-one.md"));

        result.ExitCode.Should().Be(ExitCodes.Usage);
        result.StderrFlat.Should().Contain("give each report its own --report-out");
    }

    [Fact]
    public void Report_out_without_a_report_is_a_usage_error()
    {
        using var cli = new CliHarness();

        var result = cli.Run("analyze", "--no-git", Log(),
            "--report-out", Path.Combine(cli.Home, "orphan.md"));

        result.ExitCode.Should().Be(ExitCodes.Usage);
        result.StderrFlat.Should().Contain("--report-out needs a --report");
    }

    /// <summary>Two formats aimed at one path is the same silent loss wearing a different hat.</summary>
    [Fact]
    public void Two_reports_aimed_at_the_same_file_are_rejected()
    {
        using var cli = new CliHarness();
        var path = Path.Combine(cli.Home, "clash.out");

        var result = cli.Run("analyze", "--no-git", Log(),
            "--report", "markdown", "--report-out", path,
            "--report", "sarif", "--report-out", path);

        result.ExitCode.Should().Be(ExitCodes.Usage);
        result.StderrFlat.Should().Contain("overwrite");
    }

    /// <summary>The check has to happen before the analysis, not after it has done the work.</summary>
    [Fact]
    public void An_unknown_format_is_still_rejected_when_it_is_the_second_one()
    {
        using var cli = new CliHarness();

        var result = cli.Run("analyze", "--no-git", Log(),
            "--report", "markdown", "--report-out", Path.Combine(cli.Home, "ok.md"),
            "--report", "html", "--report-out", Path.Combine(cli.Home, "bad.html"));

        result.ExitCode.Should().Be(ExitCodes.Usage);
        result.StderrFlat.Should().Contain("unknown --report 'html'");
        File.Exists(Path.Combine(cli.Home, "ok.md")).Should().BeFalse(
            "nothing should be written when the command line is rejected");
    }

    /// <summary>
    /// A clean test report produces no findings but must still produce every artifact asked for —
    /// upload-sarif fails on a missing file on exactly the runs where nothing was wrong.
    /// </summary>
    [Fact]
    public void Both_pairs_are_written_for_a_report_with_no_failures()
    {
        using var cli = new CliHarness();
        var input = Path.Combine(cli.Home, "passing.xml");
        File.WriteAllText(input, """
            <?xml version="1.0" encoding="UTF-8"?>
            <testsuites>
              <testsuite name="green" tests="1" failures="0" errors="0">
                <testcase classname="Suite" name="passes" />
              </testsuite>
            </testsuites>
            """);

        var markdown = Path.Combine(cli.Home, "green.md");
        var sarif = Path.Combine(cli.Home, "green.sarif");

        var result = cli.Run("analyze", "--no-git", "--format", "junit", input,
            "--report", "markdown", "--report-out", markdown,
            "--report", "sarif", "--report-out", sarif);

        result.ExitCode.Should().Be(ExitCodes.Ok);
        File.Exists(markdown).Should().BeTrue();
        File.Exists(sarif).Should().BeTrue();
    }
}
