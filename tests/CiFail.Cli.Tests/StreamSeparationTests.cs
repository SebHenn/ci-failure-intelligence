using System.Text.Json;
using FluentAssertions;

namespace CiFail.Cli.Tests;

/// <summary>
/// Guards the rule that makes cifail scriptable: <b>stdout carries the answer, stderr carries
/// everything about the run.</b>
///
/// <para>
/// The bug these exist for: every message used to go to stdout, so one warning about a
/// misconfigured AI provider landed inside the JSON document and broke
/// <c>cifail analyze --json | jq</c>. A single combined buffer cannot catch that, which is why
/// each test here parses stdout on its own.
/// </para>
/// </summary>
[Collection(CliCollection.Name)]
public sealed class StreamSeparationTests
{
    private static string Sample(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CiFail.sln")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "samples", name);
    }

    [Fact]
    public void Json_output_is_parseable_even_while_a_warning_is_emitted()
    {
        using var cli = new CliHarness();

        // An unknown AI provider makes the pipeline warn and continue rules-only — the exact
        // situation that used to corrupt the JSON.
        var result = cli.Run("analyze", "--no-git", "--json", "--ai", "--ai-provider", "definitely-not-a-provider",
            Sample("nuget-nu1101.log"));

        result.StderrFlat.Should().Contain("AI disabled", "the warning must still be reported");

        var parse = () => JsonDocument.Parse(result.Stdout);
        parse.Should().NotThrow("stdout must stay a single valid JSON document");
    }

    /// <summary>
    /// <c>--json</c> is always an array, one element per analysis unit — never a bare object.
    /// It used to depend on the input count, so a glob matching one file changed the document's
    /// shape and broke every consumer indexing into it.
    /// </summary>
    [Fact]
    public void Json_output_carries_nothing_but_json()
    {
        using var cli = new CliHarness();
        var result = cli.Run("analyze", "--no-git", "--json", Sample("nuget-nu1101.log"));

        var document = JsonDocument.Parse(result.Stdout);
        document.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        document.RootElement.GetArrayLength().Should().Be(1);
        document.RootElement[0].TryGetProperty("Source", out _).Should().BeTrue();
    }

    [Fact]
    public void Json_output_is_an_array_for_one_input_and_for_several()
    {
        using var cli = new CliHarness();

        var one = cli.Run("analyze", "--no-git", "--json", Sample("nuget-nu1101.log"));
        var two = cli.Run("analyze", "--no-git", "--json",
            Sample("nuget-nu1101.log"), Sample("node-eresolve.log"));

        JsonDocument.Parse(one.Stdout).RootElement.GetArrayLength().Should().Be(1);
        JsonDocument.Parse(two.Stdout).RootElement.GetArrayLength().Should().Be(2);
    }

    [Fact]
    public void Sarif_written_to_stdout_stays_valid_json()
    {
        using var cli = new CliHarness();
        var result = cli.Run("analyze", "--no-git", "--report", "sarif", Sample("nuget-nu1101.log"));

        var document = JsonDocument.Parse(result.Stdout);
        document.RootElement.GetProperty("version").GetString().Should().Be("2.1.0");
    }

    [Fact]
    public void Github_annotations_do_not_contaminate_the_json_stream()
    {
        using var cli = new CliHarness();
        var previous = Environment.GetEnvironmentVariable("GITHUB_ACTIONS");
        Environment.SetEnvironmentVariable("GITHUB_ACTIONS", "true");
        try
        {
            var junit = Path.Combine(cli.Home, "results.xml");
            File.WriteAllText(junit, """
                <testsuite name="s" tests="1" failures="1">
                  <testcase classname="C" name="t">
                    <failure message="boom">expected 1 but was 2</failure>
                  </testcase>
                </testsuite>
                """);

            var result = cli.Run("analyze", "--no-git", "--json", "--annotations", "--format", "junit", junit);

            // The runner reads workflow commands from stderr too, so the annotation still lands.
            result.Stderr.Should().Contain("::error title=");

            var parse = () => JsonDocument.Parse(result.Stdout);
            parse.Should().NotThrow("annotations must not be interleaved into the JSON document");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", previous);
        }
    }

    [Fact]
    public void Errors_never_go_to_stdout()
    {
        using var cli = new CliHarness();
        var result = cli.Run("analyze", "--no-git", Path.Combine(cli.Home, "missing.log"));

        result.Stdout.Should().BeEmpty();
        result.StderrFlat.Should().Contain("error:");
    }

    [Fact]
    public void Empty_history_explains_itself_on_stderr_so_stdout_stays_pipeable()
    {
        using var cli = new CliHarness();
        var result = cli.Run("history");

        result.ExitCode.Should().Be(ExitCodes.Ok);
        result.Stdout.Should().BeEmpty();
        result.StderrFlat.Should().Contain("No analyses recorded yet");
    }
}
