using FluentAssertions;

namespace CiFail.Cli.Tests;

/// <summary>
/// Pins the exit-code contract. Two of these are load-bearing for existing users:
/// <c>analyze</c>'s 0/1 and <c>rules validate</c>'s 0/1 are what <c>ci.yml</c> and the shipped
/// GitHub Action branch on, so they must not move even as the taxonomy grows around them.
/// </summary>
[Collection(CliCollection.Name)]
public sealed class ExitCodeTests
{
    private static string Sample(string name) =>
        Path.Combine(RepoRoot(), "samples", name);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CiFail.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("could not locate the repository root");
    }

    [Fact]
    public void Analyze_matching_log_exits_zero()
    {
        using var cli = new CliHarness();
        var result = cli.Run("analyze", "--no-git", Sample("nuget-nu1101.log"));

        result.ExitCode.Should().Be(ExitCodes.Ok);
    }

    [Fact]
    public void Analyze_missing_file_is_a_usage_error()
    {
        using var cli = new CliHarness();
        var result = cli.Run("analyze", "--no-git", Path.Combine(cli.Home, "nope.log"));

        result.ExitCode.Should().Be(ExitCodes.Usage);
        result.StderrFlat.Should().Contain("file not found");
    }

    [Fact]
    public void Unknown_type_fails_fast_and_lists_the_valid_values()
    {
        using var cli = new CliHarness();
        var result = cli.Run("analyze", "--no-git", "--type", "dotnetcore", Sample("nuget-nu1101.log"));

        // The old behaviour was to ignore the value and silently auto-detect, so the run
        // "succeeded" while analyzing as something the user never asked for.
        result.ExitCode.Should().Be(ExitCodes.Usage);
        result.StderrFlat.Should().Contain("unknown --type 'dotnetcore'");
        result.StderrFlat.Should().Contain("dotnet, node, python");
    }

    [Theory]
    [InlineData("--format", "yaml")]
    [InlineData("--report", "html")]
    public void Unknown_format_values_are_usage_errors(string flag, string value)
    {
        using var cli = new CliHarness();
        var result = cli.Run("analyze", "--no-git", flag, value, Sample("nuget-nu1101.log"));

        result.ExitCode.Should().Be(ExitCodes.Usage);
    }

    [Fact]
    public void History_with_an_unknown_id_reports_not_found()
    {
        using var cli = new CliHarness();
        var result = cli.Run("history", "9999");

        result.ExitCode.Should().Be(ExitCodes.NotFound);
        result.StderrFlat.Should().Contain("no analysis with id 9999");
    }

    [Fact]
    public void Rules_explain_with_an_unknown_id_reports_not_found()
    {
        using var cli = new CliHarness();
        var result = cli.Run("rules", "explain", "no-such-rule");

        result.ExitCode.Should().Be(ExitCodes.NotFound);
    }

    [Fact]
    public void Resolve_without_a_note_is_a_usage_error()
    {
        using var cli = new CliHarness();
        var result = cli.Run("resolve", "1");

        // Spectre's own default for a failed Validate() is 255, which reads as a crash.
        result.ExitCode.Should().Be(ExitCodes.Usage);
        result.StderrFlat.Should().Contain("resolution note is required");
    }

    [Fact]
    public void Rules_validate_still_exits_zero_on_the_shipped_packs()
    {
        using var cli = new CliHarness();
        var result = cli.Run("rules", "validate");

        result.ExitCode.Should().Be(ExitCodes.Ok);
    }

    [Fact]
    public void Rules_test_reports_a_non_match_as_a_negative_result_not_an_error()
    {
        using var cli = new CliHarness();
        var log = Path.Combine(cli.Home, "build.log");
        File.WriteAllText(log, "everything went fine\n");

        var result = cli.Run("rules", "test", "error NU1101", "--file", log);

        result.ExitCode.Should().Be(ExitCodes.Negative);
        // "no match" is the answer to what was asked, so it belongs on stdout.
        result.StdoutFlat.Should().Contain("no match");
    }

    [Fact]
    public void An_unreachable_server_reports_the_store_as_unavailable_without_a_stack_trace()
    {
        using var cli = new CliHarness();
        var result = cli.Run("history", "--server", "http://127.0.0.1:1/");

        result.ExitCode.Should().Be(ExitCodes.StoreUnavailable);
        result.StderrFlat.Should().Contain("could not reach");
        result.Stderr.Should().NotContain("   at ", "a stack trace is never the right answer for an unreachable server");
    }

    [Fact]
    public void A_malformed_server_url_is_a_usage_error_not_an_unavailable_store()
    {
        using var cli = new CliHarness();
        var result = cli.Run("history", "--server", "not a url");

        result.ExitCode.Should().Be(ExitCodes.Usage);
        result.StderrFlat.Should().Contain("http://cifail:8080", "the hint should show the expected shape");
    }

    [Fact]
    public void Debug_mode_reaches_handlers_that_report_errors_themselves()
    {
        using var cli = new CliHarness();
        Environment.SetEnvironmentVariable(Output.CliConsole.DebugEnvVar, "1");
        try
        {
            var result = cli.Run("history", "--server", "not a url");

            // CIFAIL_DEBUG promises the stack trace behind *any* error. A command that catches
            // and reports nicely must still honour it, or the switch is useless exactly where
            // someone would reach for it.
            result.Stderr.Should().Contain("UriFormatException");
        }
        finally
        {
            Environment.SetEnvironmentVariable(Output.CliConsole.DebugEnvVar, null);
        }
    }

    [Fact]
    public void An_unknown_command_is_a_usage_error()
    {
        using var cli = new CliHarness();
        var result = cli.Run("wat");

        result.ExitCode.Should().Be(ExitCodes.Usage);
        result.Stdout.Should().BeEmpty("parse errors are diagnostics, not output");
    }

    [Fact]
    public void Version_prints_the_build_version()
    {
        using var cli = new CliHarness();
        var result = cli.Run("--version");

        result.ExitCode.Should().Be(ExitCodes.Ok);
        result.StdoutFlat.Should().MatchRegex(@"\d+\.\d+\.\d+");
    }
}
