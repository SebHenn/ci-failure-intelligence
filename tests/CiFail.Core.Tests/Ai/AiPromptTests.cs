using CiFail.Core.Ai;
using CiFail.Core.Models;
using FluentAssertions;
using Xunit;

namespace CiFail.Core.Tests.Ai;

/// <summary>Covers the lenient response parsing every analyzer shares via <see cref="AiPrompt"/>.</summary>
public class AiPromptTests
{
    [Fact]
    public void Parse_reads_a_clean_json_object()
    {
        var s = AiPrompt.Parse("m", """{"rootCause": "missing dep", "fix": "run restore"}""");

        s.Should().NotBeNull();
        s!.Model.Should().Be("m");
        s.RootCause.Should().Be("missing dep");
        s.Fix.Should().Be("run restore");
    }

    [Fact]
    public void Parse_accepts_snake_case_root_cause()
    {
        var s = AiPrompt.Parse("m", """{"root_cause": "x", "fix": "y"}""");

        s!.RootCause.Should().Be("x");
        s.Fix.Should().Be("y");
    }

    [Fact]
    public void Parse_extracts_json_wrapped_in_prose()
    {
        var s = AiPrompt.Parse("m", "Here is my analysis:\n{\"rootCause\":\"a\",\"fix\":\"b\"}\nHope that helps!");

        s!.RootCause.Should().Be("a");
        s.Fix.Should().Be("b");
    }

    [Fact]
    public void Parse_falls_back_to_treating_prose_as_the_fix()
    {
        var s = AiPrompt.Parse("m", "Just reinstall the package.");

        s.Should().NotBeNull();
        s!.Fix.Should().Be("Just reinstall the package.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_returns_null_only_for_empty_input(string? text)
    {
        AiPrompt.Parse("m", text).Should().BeNull();
    }

    [Fact]
    public void Build_includes_the_ecosystem_and_the_log_excerpt()
    {
        var prompt = AiPrompt.Build(new AiRequest
        {
            LogExcerpt = "error NU1101: unable to find package Foo",
            Ecosystem = Ecosystem.Dotnet,
        });

        prompt.Should().Contain("dotnet");
        prompt.Should().Contain("NU1101");
    }
}
