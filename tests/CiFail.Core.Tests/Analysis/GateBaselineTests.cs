using CiFail.Core.Analysis;
using FluentAssertions;
using Xunit;

namespace CiFail.Core.Tests.Analysis;

/// <summary>
/// The baseline file is committed and reviewed, so its format has two jobs: survive a
/// round trip exactly, and produce a diff a human can read.
/// </summary>
public class GateBaselineTests
{
    private static GateFinding Finding(string fingerprint, string? title = "A failure")
        => new(fingerprint, fingerprint.Split(':')[0], title, new[] { "build.log" });

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\n  ")]
    [InlineData("# only comments\n# and nothing else\n")]
    public void Nothing_to_parse_means_nothing_is_accepted(string? content)
        => GateBaseline.Parse(content).Should().BeEmpty();

    [Fact]
    public void Comments_blank_lines_and_padding_are_ignored()
    {
        var accepted = GateBaseline.Parse(
            """
            # a header comment
            rule-a:1111

               rule-b:2222

            rule-c:3333    # a trailing note about this one
            """);

        accepted.Should().BeEquivalentTo(new[] { "rule-a:1111", "rule-b:2222", "rule-c:3333" });
    }

    [Fact]
    public void Windows_line_endings_parse()
    {
        // The file is committed from Windows as often as not, and a stray \r silently
        // appended to every fingerprint would make the gate fire on everything.
        GateBaseline.Parse("rule-a:1111\r\nrule-b:2222\r\n")
            .Should().BeEquivalentTo(new[] { "rule-a:1111", "rule-b:2222" });
    }

    [Fact]
    public void Render_then_parse_returns_exactly_the_same_fingerprints()
    {
        var findings = new[] { Finding("zeta:9999"), Finding("alpha:1111"), Finding("mid:5555") };

        GateBaseline.Parse(GateBaseline.Render(findings))
            .Should().BeEquivalentTo(new[] { "zeta:9999", "alpha:1111", "mid:5555" });
    }

    [Fact]
    public void Entries_are_sorted_so_regenerating_gives_a_stable_diff()
    {
        var one = GateBaseline.Render(new[] { Finding("zeta:9"), Finding("alpha:1") });
        var other = GateBaseline.Render(new[] { Finding("alpha:1"), Finding("zeta:9") });

        one.Should().Be(other, "input order must not churn a committed file");
        one.IndexOf("alpha:1", StringComparison.Ordinal)
            .Should().BeLessThan(one.IndexOf("zeta:9", StringComparison.Ordinal));
    }

    [Fact]
    public void A_duplicate_fingerprint_is_written_once()
    {
        var rendered = GateBaseline.Render(new[] { Finding("a:1"), Finding("a:1") });

        GateBaseline.Parse(rendered).Should().ContainSingle();
    }

    [Fact]
    public void The_rule_title_is_written_as_a_readable_comment()
    {
        var rendered = GateBaseline.Render(new[] { Finding("nuget-nu1101:abc", "NuGet package not found") });

        rendered.Should().Contain("nuget-nu1101:abc");
        rendered.Should().Contain("# NuGet package not found");
    }

    [Fact]
    public void A_title_with_newlines_or_hashes_cannot_corrupt_the_file()
    {
        // Rule titles are authored data. One with a newline in it would otherwise emit a
        // second line that parses as a bogus accepted fingerprint.
        var rendered = GateBaseline.Render(new[] { Finding("a:1", "broken\ntitle # with a hash") });

        GateBaseline.Parse(rendered).Should().BeEquivalentTo(new[] { "a:1" });
    }

    [Fact]
    public void A_finding_with_no_title_falls_back_to_the_rule_id()
    {
        var rendered = GateBaseline.Render(new[] { Finding("unknown:abc", title: null) });

        rendered.Should().Contain("# unknown");
        GateBaseline.Parse(rendered).Should().BeEquivalentTo(new[] { "unknown:abc" });
    }

    [Fact]
    public void An_empty_baseline_still_renders_something_explaining_itself()
    {
        var rendered = GateBaseline.Render(Array.Empty<GateFinding>());

        rendered.Should().Contain("cifail gate");
        GateBaseline.Parse(rendered).Should().BeEmpty();
    }

    [Fact]
    public void The_default_path_is_inside_the_repo_not_the_home_directory()
    {
        // It is committed, so it must never resolve under CIFAIL_HOME.
        GateBaseline.DefaultPath.Should().Be(Path.Combine(".cifail", "baseline.txt"));
        Path.IsPathRooted(GateBaseline.DefaultPath).Should().BeFalse();
    }
}
