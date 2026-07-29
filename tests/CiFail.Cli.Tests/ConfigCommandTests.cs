using System.Text.Json;
using FluentAssertions;

namespace CiFail.Cli.Tests;

/// <summary>
/// Covers <c>cifail config</c> (alias <c>doctor</c>) — including the invariant that makes it
/// safe to paste into a bug report: <b>it never prints a secret.</b>
/// </summary>
[Collection(CliCollection.Name)]
public sealed class ConfigCommandTests
{
    /// <summary>Values planted in the config below that must never reach either stream.</summary>
    private const string PlantedPassword = "sup3r-s3cret-passw0rd";
    private const string PlantedWebhookPath = "T00000000/B11111111/xxxxSECRETxxxx";

    private static void WriteSecretLadenConfig(CliHarness cli) => cli.WriteConfig($"""
        database:
          provider: sqlite
          connectionString: "Host=db;Username=cifail;Password={PlantedPassword}"
        notifications:
          slackWebhookUrl: "https://hooks.slack.com/services/{PlantedWebhookPath}"
        """);

    [Fact]
    public void Reports_paths_and_build_flavor()
    {
        using var cli = new CliHarness();
        var result = cli.Run("config");

        result.ExitCode.Should().Be(ExitCodes.Ok);
        result.StdoutFlat.Should().Contain("cifail");
        result.StdoutFlat.Should().Contain("history.db");
        result.StdoutFlat.Should().MatchRegex("(slim|full) build");
    }

    [Fact]
    public void Doctor_is_an_alias_for_config()
    {
        using var cli = new CliHarness();

        cli.Run("doctor").ExitCode.Should().Be(ExitCodes.Ok);
    }

    [Fact]
    public void Never_prints_a_connection_string_password_or_webhook_url()
    {
        using var cli = new CliHarness();
        WriteSecretLadenConfig(cli);

        var result = cli.Run("config");

        result.ExitCode.Should().Be(ExitCodes.Ok);
        (result.Stdout + result.Stderr).Should().NotContain(PlantedPassword);
        (result.Stdout + result.Stderr).Should().NotContain(PlantedWebhookPath);

        // ...while still saying that they are configured, which is the useful half.
        result.StdoutFlat.Should().Contain("set");
    }

    [Fact]
    public void Json_output_never_leaks_secrets_either()
    {
        using var cli = new CliHarness();
        WriteSecretLadenConfig(cli);

        var result = cli.Run("config", "--json");

        result.Stdout.Should().NotContain(PlantedPassword);
        result.Stdout.Should().NotContain(PlantedWebhookPath);

        var root = JsonDocument.Parse(result.Stdout).RootElement;
        root.GetProperty("Version").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("Paths").GetProperty("HistoryDb").GetString().Should().Contain("history.db");
        root.GetProperty("StoreProviders").EnumerateArray().Select(e => e.GetString())
            .Should().Contain("sqlite");
    }

    [Fact]
    public void Shows_where_each_value_came_from()
    {
        using var cli = new CliHarness();
        cli.WriteConfig("""
            ai:
              provider: openai
            """);

        var result = cli.Run("config", "--json");
        var ai = JsonDocument.Parse(result.Stdout).RootElement.GetProperty("Ai");

        var provider = ai.EnumerateArray().First(e => e.GetProperty("Name").GetString() == "provider");
        provider.GetProperty("Value").GetString().Should().Be("openai");
        provider.GetProperty("Source").GetString().Should().Be("config.yaml");
    }

    [Fact]
    public void Environment_wins_over_the_file_and_says_so()
    {
        using var cli = new CliHarness();
        cli.WriteConfig("""
            ai:
              provider: openai
            """);

        Environment.SetEnvironmentVariable("CIFAIL_AI_PROVIDER", "anthropic");
        try
        {
            var result = cli.Run("config", "--json");
            var ai = JsonDocument.Parse(result.Stdout).RootElement.GetProperty("Ai");
            var provider = ai.EnumerateArray().First(e => e.GetProperty("Name").GetString() == "provider");

            // "I edited config.yaml and nothing changed" is almost always this.
            provider.GetProperty("Value").GetString().Should().Be("anthropic");
            provider.GetProperty("Source").GetString().Should().Be("CIFAIL_AI_PROVIDER");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CIFAIL_AI_PROVIDER", null);
        }
    }

    [Fact]
    public void Reports_a_misspelled_setting_with_a_suggestion()
    {
        using var cli = new CliHarness();
        cli.WriteConfig("""
            notifications:
              slackWebhoookUrl: "https://example.com/hook"
            """);

        var result = cli.Run("config");

        // The loader ignores unknown keys on purpose (forward compatibility), so this is the
        // only place the typo can ever surface.
        result.StdoutFlat.Should().Contain("notifications.slackWebhoookUrl");
        result.StdoutFlat.Should().Contain("slackWebhookUrl");
        result.ExitCode.Should().Be(ExitCodes.Ok, "an unknown key is a warning, not an error");
    }

    [Fact]
    public void Strict_turns_warnings_into_a_non_zero_exit()
    {
        using var cli = new CliHarness();
        cli.WriteConfig("""
            notifications:
              slackWebhoookUrl: "https://example.com/hook"
            """);

        cli.Run("config", "--strict").ExitCode.Should().Be(ExitCodes.Config);
    }

    [Fact]
    public void An_invalid_value_is_an_error_even_without_strict()
    {
        using var cli = new CliHarness();
        cli.WriteConfig("""
            notifications:
              webhookUrl: "not-a-url"
            """);

        var result = cli.Run("config");

        result.ExitCode.Should().Be(ExitCodes.Config);
        result.StdoutFlat.Should().Contain("absolute http(s) URL");
    }

    [Fact]
    public void Malformed_yaml_is_reported_against_the_file_rather_than_crashing()
    {
        using var cli = new CliHarness();
        cli.WriteConfig("database:\n  provider: [unclosed\n");

        var result = cli.Run("config");

        result.ExitCode.Should().Be(ExitCodes.Config);
        result.Stdout.Should().NotContain("   at ", "a YAML typo is not a crash");
    }

    [Fact]
    public void A_command_that_reads_config_fails_cleanly_on_malformed_yaml()
    {
        using var cli = new CliHarness();
        cli.WriteConfig("database:\n  provider: [unclosed\n");

        var result = cli.Run("history");

        result.ExitCode.Should().Be(ExitCodes.Config);
        result.StderrFlat.Should().Contain("config.yaml");
        result.Stderr.Should().NotContain("YamlDotNet", "the raw parser exception must not reach the user");
    }
}
