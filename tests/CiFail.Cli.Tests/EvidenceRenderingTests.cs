using FluentAssertions;
using Xunit;

namespace CiFail.Cli.Tests;

/// <summary>
/// What the report shows as evidence (R34).
///
/// <para>
/// Assertions here stay <b>glyph-agnostic</b>: the marker next to the matched line renders as
/// Unicode on Linux CI and as an ASCII fallback on the Windows test host, so nothing may depend
/// on which one appeared.
/// </para>
/// </summary>
[Collection(CliCollection.Name)]
public class EvidenceRenderingTests
{
    private const string Log = """
        Restoring packages
        Compiling 3 files
        src/app.ts(12,5): error TS2345: Argument of type 'string'.
          Type 'string' is not assignable to type 'number'.
        Build failed
        """;

    private static string WriteLog(CliHarness cli, string name = "build.log")
    {
        var path = Path.Combine(cli.Home, name);
        File.WriteAllText(path, Log);
        return path;
    }

    /// <summary>
    /// The detail line under a compiler error is the part that says what to change, and the
    /// single-line report threw it away.
    /// </summary>
    [Fact]
    public void The_panel_shows_the_lines_around_the_match()
    {
        using var cli = new CliHarness();

        var result = cli.Run("analyze", "--no-history", "--no-git", WriteLog(cli));

        result.StdoutFlat.Should().Contain("is not assignable");
        result.StdoutFlat.Should().Contain("Compiling 3 files");
    }

    [Fact]
    public void The_panel_names_the_line_number()
    {
        using var cli = new CliHarness();

        var result = cli.Run("analyze", "--no-history", "--no-git", WriteLog(cli));

        result.StdoutFlat.Should().Contain("line 3", "the report has to be locatable in the log");
    }

    /// <summary>
    /// Both identifiers every follow-up command needs. Neither appeared anywhere in the human
    /// view before — you could read the whole report and still not know what to pass to
    /// `cifail rules explain`, or what a gate baseline would key on.
    /// </summary>
    [Fact]
    public void The_report_shows_the_rule_id_and_the_fingerprint()
    {
        using var cli = new CliHarness();

        var result = cli.Run("analyze", "--no-history", "--no-git", WriteLog(cli));

        result.StdoutFlat.Should().Contain("typescript-compile-error");
        result.StdoutFlat.Should().Contain("Fingerprint");
        result.StdoutFlat.Should().Contain("cifail rules explain");
    }

    [Fact]
    public void Context_zero_falls_back_to_the_matched_line_alone()
    {
        using var cli = new CliHarness();

        var result = cli.Run("analyze", "--context", "0", "--no-history", "--no-git", WriteLog(cli));

        result.StdoutFlat.Should().Contain("error TS2345");
        result.StdoutFlat.Should().NotContain("is not assignable",
            "--context 0 is the pre-R34 behaviour, for anyone who wants the terse report back");
    }

    [Fact]
    public void Json_carries_the_line_number_and_context()
    {
        using var cli = new CliHarness();

        var result = cli.Run("analyze", "--json", "--no-history", "--no-git", WriteLog(cli));

        result.Stdout.Should().Contain("\"LineNumber\": 3");
        result.Stdout.Should().Contain("ContextAfter");
    }
}
