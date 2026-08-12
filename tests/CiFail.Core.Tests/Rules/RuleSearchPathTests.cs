using CiFail.Core.Configuration;
using CiFail.Core.Rules;
using FluentAssertions;
using Xunit;

namespace CiFail.Core.Tests.Rules;

/// <summary>
/// Where user rule packs are loaded from. The point of the search path is that a repository can
/// ship the rules that only make sense inside it, without repointing <c>CIFAIL_HOME</c> at the
/// checkout (which also moves the history database).
/// </summary>
public class RuleSearchPathTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cifail-rulepath-" + Guid.NewGuid().ToString("N"));

    private string Dir(params string[] parts)
    {
        var path = Path.Combine(new[] { _root }.Concat(parts).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // ---- discovery -------------------------------------------------------------------------

    [Fact]
    public void Finds_the_repo_pack_from_a_subdirectory()
    {
        // Walking up is what makes it work with no configuration: you rarely run a build from
        // the repo root, and the rules belong to the repo, not to the directory you happen to be in.
        var rules = Dir("repo", ".cifail", "rules");
        var deep = Dir("repo", "src", "widgets", "tests");

        RuleSearchPath.DiscoverRepoRules(deep).Should().Be(rules);
    }

    [Fact]
    public void Finds_nothing_outside_a_repo()
    {
        RuleSearchPath.DiscoverRepoRules(Dir("plain")).Should().BeNull();
    }

    [Fact]
    public void Finds_the_nearest_repo_pack_when_they_nest()
    {
        Dir("outer", ".cifail", "rules");
        var inner = Dir("outer", "inner", ".cifail", "rules");

        RuleSearchPath.DiscoverRepoRules(Dir("outer", "inner", "src")).Should().Be(inner);
    }

    // ---- ordering --------------------------------------------------------------------------

    [Fact]
    public void Orders_locations_from_most_general_to_most_explicit()
    {
        // Later wins on a duplicate rule id, so this order is the override policy: an explicit
        // --rules beats config.yaml, which beats the repo's own pack, which beats your home dir.
        var repo = Dir("repo", ".cifail", "rules");
        var configured = Dir("configured");
        var cli = Dir("cli");
        var config = new CiFailConfig { Rules = { Paths = { configured } } };

        var resolved = RuleSearchPath.Resolve(new[] { cli }, config, Dir("repo", "src"));

        resolved.Should().HaveCount(4);
        resolved[0].Should().Be(CiFailPaths.UserRulesDir);
        resolved[1].Should().Be(repo);
        resolved[2].Should().Be(configured);
        resolved[3].Should().Be(cli);
    }

    [Fact]
    public void Lists_a_configured_directory_that_does_not_exist()
    {
        // Kept rather than filtered, so `cifail config` can show it — a path that isn't there is
        // the most common reason a rule "didn't load", and silently dropping it hides that.
        var missing = Path.Combine(_root, "nope");
        var config = new CiFailConfig { Rules = { Paths = { missing } } };

        RuleSearchPath.Resolve(config: config, workingDirectory: Dir("plain")).Should().Contain(missing);
    }

    [Fact]
    public void Resolves_relative_paths_and_drops_duplicates()
    {
        var configured = Dir("shared");
        var config = new CiFailConfig { Rules = { Paths = { configured, configured + Path.DirectorySeparatorChar } } };

        var resolved = RuleSearchPath.Resolve(new[] { configured }, config, Dir("plain"));

        resolved.Should().ContainSingle(d => d == configured);
        resolved.Should().OnlyContain(d => Path.IsPathFullyQualified(d));
    }

    [Fact]
    public void Ignores_an_unusable_configured_path()
    {
        // A junk entry must not take the rest of the search path down with it.
        var config = new CiFailConfig { Rules = { Paths = { "   ", "\0bad" } } };

        var resolved = RuleSearchPath.Resolve(config: config, workingDirectory: Dir("plain"));

        resolved.Should().ContainSingle().Which.Should().Be(CiFailPaths.UserRulesDir);
    }

    // ---- CIFAIL_RULES parsing --------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Splits_an_empty_list_to_nothing(string? value) =>
        RuleSearchPath.SplitList(value).Should().BeEmpty();

    [Fact]
    public void Splits_a_path_style_list()
    {
        var value = string.Join(Path.PathSeparator, new[] { "  /a/b  ", "", "/c/d" });

        RuleSearchPath.SplitList(value).Should().Equal("/a/b", "/c/d");
    }

    // ---- loading ---------------------------------------------------------------------------

    [Fact]
    public void A_later_directory_overrides_an_earlier_rule_with_the_same_id()
    {
        var first = Dir("first");
        var second = Dir("second");
        File.WriteAllText(Path.Combine(first, "pack.yaml"), Pack("dup", "first title"));
        File.WriteAllText(Path.Combine(second, "pack.yaml"), Pack("dup", "second title"));

        var rules = RulePackLoader.LoadFrom(new[] { first, second });

        rules.Should().ContainSingle(r => r.Id == "dup")
            .Which.Title.Should().Be("second title");
    }

    [Fact]
    public void A_user_pack_still_overrides_an_embedded_rule()
    {
        var dir = Dir("override");
        File.WriteAllText(Path.Combine(dir, "pack.yaml"), Pack("github-actions-error", "mine"));

        RulePackLoader.LoadFrom(new[] { dir })
            .Should().ContainSingle(r => r.Id == "github-actions-error")
            .Which.Title.Should().Be("mine");
    }

    [Fact]
    public void A_broken_pack_does_not_stop_the_others_loading()
    {
        // cifail now *finds* packs it wasn't pointed at, so a repo with one bad file must not
        // break analysis for everyone who works in it. `rules validate` is where it gets named.
        var dir = Dir("mixed");
        File.WriteAllText(Path.Combine(dir, "a-broken.yaml"), "not: [valid: yaml");
        File.WriteAllText(Path.Combine(dir, "b-good.yaml"), Pack("still-loads", "fine"));

        var load = () => RulePackLoader.LoadFrom(new[] { dir });

        load.Should().NotThrow();
        load().Should().Contain(r => r.Id == "still-loads");
    }

    [Fact]
    public void Validation_covers_every_directory_on_the_search_path()
    {
        var dir = Dir("validated");
        File.WriteAllText(Path.Combine(dir, "pack.yaml"), "- id: no-match-pattern\n  title: incomplete\n");

        var result = RulePackValidator.ValidateAll(dir);

        result.Diagnostics.Should().Contain(d =>
            d.RuleId == "no-match-pattern" && d.Message.Contains("match"));
    }

    private static string Pack(string id, string title) =>
        $"- id: {id}\n" +
        $"  ecosystem: generic\n" +
        $"  category: build\n" +
        $"  title: {title}\n" +
        $"  match: 'a-very-specific-marker-{id}'\n" +
        $"  confidence: 0.9\n" +
        $"  fix: do the thing\n" +
        $"  docs: https://example.com/{id}\n";
}
