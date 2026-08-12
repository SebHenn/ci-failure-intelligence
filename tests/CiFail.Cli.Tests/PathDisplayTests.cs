using System.Text;
using CiFail.Cli.Output;
using CiFail.Core.Models;
using FluentAssertions;
using Spectre.Console;
using Xunit;

namespace CiFail.Cli.Tests;

/// <summary>
/// The report header names the log it is about. It used to lose that name entirely for an
/// absolute path — which is exactly what CI systems pass — leaving every report in a job that
/// analyzed several logs looking identical.
/// </summary>
public class PathDisplayTests
{
    [Fact]
    public void Leaves_a_label_that_already_fits()
    {
        PathDisplay.Elide("samples/nuget-nu1101.log", 40).Should().Be("samples/nuget-nu1101.log");
    }

    [Fact]
    public void Keeps_the_file_name_when_it_has_to_cut()
    {
        var elided = PathDisplay.Elide(@"C:\Users\sebhe\Desktop\ci-failure-intelligence\samples\nuget-nu1101.log", 40);

        // The end of a path identifies it; the drive and home directory are the disposable part.
        elided.Should().EndWith(@"nuget-nu1101.log");
        elided.Length.Should().BeLessOrEqualTo(40);
        elided.Should().StartWith(Glyphs.Ellipsis);
    }

    [Fact]
    public void Cuts_at_a_directory_boundary_when_one_is_near()
    {
        var elided = PathDisplay.Elide("/home/runner/work/repo/repo/build.log", 30);

        elided.Should().Be(Glyphs.Ellipsis + "/work/repo/repo/build.log");
    }

    [Fact]
    public void Keeps_the_test_name_from_an_expanded_report_source()
    {
        // R17 sources are "<path>::<FullName>"; the test name is the half that says which
        // failure this is, and it sits at the end.
        var elided = PathDisplay.Elide("/very/long/path/to/results.trx::Acme.Tests.WidgetTests.Explodes", 40);

        elided.Should().EndWith("Acme.Tests.WidgetTests.Explodes");
    }

    [Fact]
    public void Degrades_rather_than_throwing_at_absurd_widths()
    {
        PathDisplay.Elide("build.log", 0).Should().BeEmpty();
        PathDisplay.Elide("build.log", -5).Should().BeEmpty();
        PathDisplay.Elide("build.log", 2).Should().Be("og");
        PathDisplay.Elide("", 20).Should().BeEmpty();
    }

    [Fact]
    public void The_header_still_names_the_log_on_a_narrow_console()
    {
        // The regression itself: Spectre word-wraps a Rule title and keeps only the first line,
        // so an absolute path — one long word — collapsed to "cifail ·…".
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(writer),
        });
        console.Profile.Width = 80;
        console.Profile.Encoding = Encoding.UTF8;

        ConsoleRenderer.Render(console, NoMatch(@"C:\Users\sebhe\Desktop\ci-failure-intelligence\samples\build.log"));

        var header = writer.ToString().Split('\n')[0];
        header.Should().Contain("build.log");
        header.Should().Contain("cifail");
    }

    private static Analysis NoMatch(string source) => new()
    {
        Source = source,
        Ecosystem = Ecosystem.Generic,
        Matches = Array.Empty<RuleMatch>(),
        Fingerprint = new FailureFingerprint { RuleId = "unknown", Hash = "0000" },
    };
}
