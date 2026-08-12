using System.Text.Json;
using FluentAssertions;

namespace CiFail.Cli.Tests;

/// <summary>
/// End-to-end coverage of <c>cifail gate</c> — the adopt-it-on-a-broken-repo workflow, which
/// is the whole point: baseline the existing backlog once, then only a <i>new</i> failure
/// breaks the build.
///
/// <para>
/// Every test passes <c>--baseline</c> explicitly. The default is <c>.cifail/baseline.txt</c>
/// relative to the working directory, and the test host's working directory is process-global
/// — mutating it here would leak into every other test in the collection.
/// </para>
/// </summary>
[Collection(CliCollection.Name)]
public sealed class GateCommandTests
{
    private static string Sample(string name) => TestPaths.Sample(name);

    private static string Fixture(string name) => TestPaths.Fixture(name);

    private static string BaselineIn(CliHarness cli) => Path.Combine(cli.Home, "baseline.txt");

    [Fact]
    public void With_no_baseline_every_failure_is_new_and_the_gate_fails()
    {
        using var cli = new CliHarness();
        var result = cli.Run("gate", "--baseline", BaselineIn(cli), Sample("nuget-nu1101.log"));

        result.ExitCode.Should().Be(ExitCodes.Negative);
        result.StdoutFlat.Should().Contain("NuGet package not found");
        result.StderrFlat.Should().Contain("--update", "the first run has to say how to adopt it");
    }

    [Fact]
    public void Update_writes_a_baseline_and_succeeds()
    {
        using var cli = new CliHarness();
        var baseline = BaselineIn(cli);

        var result = cli.Run("gate", "--update", "--baseline", baseline, Sample("nuget-nu1101.log"));

        result.ExitCode.Should().Be(ExitCodes.Ok);
        File.Exists(baseline).Should().BeTrue();
        File.ReadAllText(baseline).Should().Contain("nuget-nu1101:");
    }

    [Fact]
    public void A_baselined_failure_then_passes()
    {
        using var cli = new CliHarness();
        var baseline = BaselineIn(cli);
        cli.Run("gate", "--update", "--baseline", baseline, Sample("nuget-nu1101.log"));

        var result = cli.Run("gate", "--baseline", baseline, Sample("nuget-nu1101.log"));

        result.ExitCode.Should().Be(ExitCodes.Ok);
        result.StdoutFlat.Should().Contain("No new failures");
    }

    [Fact]
    public void A_failure_that_is_not_in_the_baseline_fails_the_gate()
    {
        using var cli = new CliHarness();
        var baseline = BaselineIn(cli);
        cli.Run("gate", "--update", "--baseline", baseline, Sample("nuget-nu1101.log"));

        var result = cli.Run("gate", "--baseline", baseline, Fixture("node-eresolve.log"));

        result.ExitCode.Should().Be(ExitCodes.Negative);
        result.StdoutFlat.Should().Contain("ERESOLVE");
    }

    [Fact]
    public void Deleting_a_line_from_the_baseline_re_arms_the_gate()
    {
        // The documented way to stop accepting a failure. If hand-editing didn't work, the
        // file being human-readable would be pointless.
        using var cli = new CliHarness();
        var baseline = BaselineIn(cli);
        cli.Run("gate", "--update", "--baseline", baseline, Sample("nuget-nu1101.log"));

        var kept = File.ReadAllLines(baseline)
            .Where(l => !l.Contains("nuget-nu1101", StringComparison.Ordinal));
        File.WriteAllLines(baseline, kept);

        var result = cli.Run("gate", "--baseline", baseline, Sample("nuget-nu1101.log"));

        result.ExitCode.Should().Be(ExitCodes.Negative);
    }

    [Fact]
    public void Json_goes_to_stdout_and_stays_parseable_while_hints_go_to_stderr()
    {
        using var cli = new CliHarness();
        var result = cli.Run("gate", "--json", "--baseline", BaselineIn(cli), Sample("nuget-nu1101.log"));

        result.ExitCode.Should().Be(ExitCodes.Negative);

        using var doc = JsonDocument.Parse(result.Stdout);
        doc.RootElement.GetProperty("Passed").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("NewCount").GetInt32().Should().Be(1);
        doc.RootElement.GetProperty("New")[0].GetProperty("RuleId").GetString().Should().Be("nuget-nu1101");
    }

    [Fact]
    public void A_baseline_entry_that_no_longer_occurs_is_reported_but_never_fails_the_gate()
    {
        using var cli = new CliHarness();
        var baseline = BaselineIn(cli);
        cli.Run("gate", "--update", "--baseline", baseline,
            Sample("nuget-nu1101.log"), Fixture("node-eresolve.log"));

        var result = cli.Run("gate", "--baseline", baseline, Sample("nuget-nu1101.log"));

        result.ExitCode.Should().Be(ExitCodes.Ok);
        result.StderrFlat.Should().Contain("did not occur in this run");
    }

    [Fact]
    public void A_report_with_no_failing_tests_passes()
    {
        using var cli = new CliHarness();
        var report = Path.Combine(cli.Home, "clean.trx");
        File.WriteAllText(report,
            """
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testName="Passes" outcome="Passed" />
              </Results>
            </TestRun>
            """);

        var result = cli.Run("gate", "--baseline", BaselineIn(cli), report);

        result.ExitCode.Should().Be(ExitCodes.Ok);
    }

    [Fact]
    public void An_unknown_type_is_a_usage_error()
    {
        using var cli = new CliHarness();
        var result = cli.Run("gate", "--type", "dotnetcore", "--baseline", BaselineIn(cli), Sample("nuget-nu1101.log"));

        result.ExitCode.Should().Be(ExitCodes.Usage);
        result.StderrFlat.Should().Contain("unknown --type");
    }

    [Fact]
    public void No_input_is_a_usage_error_that_says_what_to_do()
    {
        using var cli = new CliHarness();
        var result = cli.Run("gate", "--baseline", BaselineIn(cli));

        result.ExitCode.Should().Be(ExitCodes.Usage);
        result.StderrFlat.Should().Contain("cifail gate build.log");
    }

    [Fact]
    public void An_unwritable_baseline_path_is_a_usage_error_not_a_crash()
    {
        using var cli = new CliHarness();
        // A directory where a file should be: writing it must fail cleanly.
        var occupied = Path.Combine(cli.Home, "occupied");
        Directory.CreateDirectory(occupied);

        var result = cli.Run("gate", "--update", "--baseline", occupied, Sample("nuget-nu1101.log"));

        result.ExitCode.Should().Be(ExitCodes.Usage);
        result.StderrFlat.Should().Contain("could not write");
    }
}
