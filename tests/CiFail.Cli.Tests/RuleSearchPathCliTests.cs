using CiFail.Core.Configuration;
using FluentAssertions;

namespace CiFail.Cli.Tests;

/// <summary>
/// Loading rule packs a repository ships itself, without repointing <c>CIFAIL_HOME</c> at the
/// checkout — the repo case is what custom rules are mostly for, and moving the home directory
/// to get it also moves <c>history.db</c>.
///
/// <para>
/// Nothing here changes the working directory: the test host's is process-global, so the
/// <c>.cifail/rules</c> auto-discovery path is covered in Core (<c>RuleSearchPathTests</c>) and
/// what these assert is the wiring a user actually types — the flag, the env var, the file.
/// </para>
/// </summary>
[Collection(CliCollection.Name)]
public sealed class RuleSearchPathCliTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cifail-rules-cli-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousRules = Environment.GetEnvironmentVariable(ConfigLoader.RulesEnvVar);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ConfigLoader.RulesEnvVar, _previousRules);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>A pack whose rule beats every shipped one on this log, so "it loaded" is visible.</summary>
    private string PackDir(string name = "rules")
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "repo.yaml"),
            "- id: repo-determinism-broken\n" +
            "  ecosystem: generic\n" +
            "  category: build\n" +
            "  title: Determinism contract broken\n" +
            "  match: '(?<layer>\\w+)\\.png differs between two runs of the same seed'\n" +
            "  confidence: 0.95\n" +
            "  fix: The {layer} layer is not deterministic for a fixed seed.\n" +
            "  docs: https://example.com/determinism\n");
        return dir;
    }

    private static string Log() => TestPaths.Fixture("github-actions-annotation.log");

    [Fact]
    public void Analyze_loads_a_pack_from_the_rules_flag()
    {
        using var cli = new CliHarness();

        var result = cli.Run("analyze", "--rules", PackDir(), "--no-history", "--no-git", Log());

        result.StdoutFlat.Should().Contain("Determinism contract broken");
        result.StdoutFlat.Should().Contain("The climate layer is not deterministic",
            "captures from a user pack interpolate like any other rule");
    }

    [Fact]
    public void Analyze_loads_a_pack_from_CIFAIL_RULES()
    {
        using var cli = new CliHarness();
        Environment.SetEnvironmentVariable(ConfigLoader.RulesEnvVar, PackDir());

        var result = cli.Run("analyze", "--no-history", "--no-git", Log());

        result.StdoutFlat.Should().Contain("Determinism contract broken");
    }

    [Fact]
    public void Analyze_loads_a_pack_named_in_config_yaml()
    {
        using var cli = new CliHarness();
        cli.WriteConfig($"rules:\n  paths:\n    - {PackDir().Replace("\\", "/")}\n");

        var result = cli.Run("analyze", "--no-history", "--no-git", Log());

        result.StdoutFlat.Should().Contain("Determinism contract broken");
    }

    [Fact]
    public void Gate_uses_the_same_packs_so_the_verdict_matches_the_report()
    {
        using var cli = new CliHarness();
        var baseline = Path.Combine(cli.Home, "baseline.txt");

        var result = cli.Run("gate", "--rules", PackDir(), "--baseline", baseline, Log());

        result.ExitCode.Should().Be(ExitCodes.Negative);
        result.StdoutFlat.Should().Contain("Determinism contract broken");
        result.StdoutFlat.Should().Contain("repo-determinism-broken:",
            "the fingerprint has to come from the rule that actually matched");
    }

    [Fact]
    public void A_missing_rules_directory_is_reported_rather_than_silently_ignored()
    {
        using var cli = new CliHarness();
        var missing = Path.Combine(_root, "not-there");

        var result = cli.Run("analyze", "--rules", missing, "--no-history", "--no-git", Log());

        // "No rule matched" and "the pack never loaded" look identical in the report otherwise.
        result.StderrFlat.Should().Contain("rules directory not found");
        result.ExitCode.Should().Be(ExitCodes.Ok, "a bad --rules is a warning, not a failed analysis");
    }

    /// <summary>
    /// `rules explain` used to decide "is this a user rule?" by looking only in
    /// <c>~/.cifail/rules</c>, while loading the rule itself from the full search path — so a
    /// pack reached through <c>CIFAIL_RULES</c>, <c>config.yaml</c>, or a repository's own
    /// <c>.cifail/rules</c> was reported as an <c>embedded default</c>. "Where does this rule
    /// live?" is the question the command exists to answer.
    /// </summary>
    [Fact]
    public void Rules_explain_does_not_call_a_user_pack_an_embedded_default()
    {
        using var cli = new CliHarness();
        Environment.SetEnvironmentVariable(ConfigLoader.RulesEnvVar, PackDir());

        var result = cli.Run("rules", "explain", "repo-determinism-broken");

        result.ExitCode.Should().Be(ExitCodes.Ok);
        result.StdoutFlat.Should().NotContain("embedded default");
        result.StdoutFlat.Should().Contain("Determinism contract broken");
    }

    [Fact]
    public void Rules_explain_still_calls_a_shipped_rule_an_embedded_default()
    {
        using var cli = new CliHarness();

        var result = cli.Run("rules", "explain", "nuget-nu1101");

        result.ExitCode.Should().Be(ExitCodes.Ok);
        result.StdoutFlat.Should().Contain("embedded default");
    }

    [Fact]
    public void Rules_explain_reports_a_user_pack_that_overrides_a_shipped_rule()
    {
        using var cli = new CliHarness();
        var dir = Path.Combine(_root, "override");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "override.yaml"),
            "- id: nuget-nu1101\n" +
            "  ecosystem: dotnet\n" +
            "  category: dependency\n" +
            "  title: Our own NU1101 wording\n" +
            "  match: 'NU1101'\n" +
            "  confidence: 0.95\n" +
            "  fix: Ask the platform team.\n" +
            "  docs: https://example.com/nu1101\n");
        Environment.SetEnvironmentVariable(ConfigLoader.RulesEnvVar, dir);

        var result = cli.Run("rules", "explain", "nuget-nu1101");

        result.StdoutFlat.Should().Contain("overrides an embedded default");
        result.StdoutFlat.Should().Contain("Our own NU1101 wording");
    }

    [Fact]
    public void Rules_list_shows_every_directory_it_searched()
    {
        using var cli = new CliHarness();

        var result = cli.Run("rules", "list", "--rules", PackDir());

        result.StdoutFlat.Should().Contain("repo-determinism-broken");
        result.StderrFlat.Should().Contain("user rule packs are loaded from");
        result.StderrFlat.Should().Contain("(1 pack)");
    }

    [Fact]
    public void Config_reports_a_configured_directory_that_is_not_there()
    {
        using var cli = new CliHarness();
        var missing = Path.Combine(_root, "gone");
        cli.WriteConfig($"rules:\n  paths:\n    - {missing.Replace("\\", "/")}\n");

        var result = cli.Run("config");

        result.ExitCode.Should().Be(ExitCodes.Ok, "a missing pack directory is a warning, not an error");
        result.StdoutFlat.Should().Contain("does not exist");
    }
}
