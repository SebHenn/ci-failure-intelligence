using CiFail.Core.Ai;
using FluentAssertions;
using Xunit;

namespace CiFail.Core.Tests.Ai;

/// <summary>Lenient parsing of a model's rule-draft JSON (R23).</summary>
public class AiRuleDraftParserTests
{
    [Fact]
    public void Parses_a_clean_json_draft()
    {
        var json = """
            {"id":"npm-foo","ecosystem":"node","category":"dependency",
             "title":"Foo failed","match":"Cannot find (?<m>\\w+)","fix":"install it","docs":"https://x"}
            """;

        var draft = AiRuleDraftParser.Parse("test/model", json);

        draft.Should().NotBeNull();
        draft!.Id.Should().Be("npm-foo");
        draft.Ecosystem.Should().Be("node");
        draft.Match.Should().Be(@"Cannot find (?<m>\w+)");
        draft.Fix.Should().Be("install it");
        draft.Docs.Should().Be("https://x");
        draft.Model.Should().Be("test/model");
    }

    [Fact]
    public void Extracts_json_wrapped_in_prose()
    {
        var text = "Sure! Here is the rule:\n```json\n{\"title\":\"X\",\"match\":\"boom\"}\n```\nHope that helps.";

        var draft = AiRuleDraftParser.Parse("m", text);

        draft.Should().NotBeNull();
        draft!.Title.Should().Be("X");
        draft.Match.Should().Be("boom");
    }

    [Fact]
    public void Defaults_missing_optional_fields()
    {
        var draft = AiRuleDraftParser.Parse("m", "{\"match\":\"boom\"}");

        draft.Should().NotBeNull();
        draft!.Ecosystem.Should().Be("generic");
        draft.Category.Should().Be("general");
        draft.Id.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no json here at all")]
    [InlineData("{\"ecosystem\":\"node\"}")] // neither match nor title
    public void Returns_null_when_there_is_no_usable_draft(string text)
    {
        AiRuleDraftParser.Parse("m", text).Should().BeNull();
    }
}
