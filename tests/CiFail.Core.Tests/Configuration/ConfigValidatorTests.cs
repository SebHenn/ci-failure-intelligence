using CiFail.Core.Configuration;
using CiFail.Core.Rules;
using FluentAssertions;

namespace CiFail.Core.Tests;

/// <summary>
/// Covers the config linter. Its reason to exist: the loader keeps
/// <c>IgnoreUnmatchedProperties()</c> so a newer config never breaks an older cifail, which means
/// a typo is silently dropped and the user only finds out when the feature never fires.
/// </summary>
public sealed class ConfigValidatorTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public ConfigValidatorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cifail-config-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "config.yaml");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private IReadOnlyList<ConfigDiagnostic> Validate(string yaml)
    {
        File.WriteAllText(_path, yaml);
        return ConfigValidator.Validate(_path);
    }

    [Fact]
    public void A_missing_file_is_valid()
    {
        ConfigValidator.Validate(Path.Combine(_dir, "absent.yaml")).Should().BeEmpty();
    }

    [Fact]
    public void An_empty_file_is_valid()
    {
        Validate("").Should().BeEmpty();
    }

    [Fact]
    public void A_fully_populated_valid_config_produces_no_diagnostics()
    {
        var diagnostics = Validate("""
            database:
              provider: sqlite
            ai:
              provider: ollama
              model: llama3
              embeddings: true
              embeddingDimensions: 768
              limits:
                maxCallsPerRun: 10
            notifications:
              events: [new-failure, resolved]
              slackWebhookUrl: https://hooks.slack.com/services/x/y/z
              dedupeSeconds: 60
            """);

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void An_unknown_key_warns_and_suggests_the_intended_one()
    {
        var diagnostics = Validate("""
            notifications:
              slackWebhoookUrl: https://example.com/x
            """);

        diagnostics.Should().ContainSingle();
        diagnostics[0].Severity.Should().Be(DiagnosticSeverity.Warning);
        diagnostics[0].Path.Should().Be("notifications.slackWebhoookUrl");
        diagnostics[0].Message.Should().Contain("slackWebhookUrl");
        diagnostics[0].Line.Should().Be(2);
    }

    [Fact]
    public void A_wrong_case_key_is_reported_because_the_loader_matches_case_sensitively()
    {
        var diagnostics = Validate("""
            database:
              connectionstring: whatever
            """);

        diagnostics.Should().ContainSingle()
            .Which.Message.Should().Contain("connectionString");
    }

    [Fact]
    public void An_unknown_top_level_key_is_reported()
    {
        var diagnostics = Validate("""
            databse:
              provider: sqlite
            """);

        diagnostics.Should().ContainSingle();
        diagnostics[0].Path.Should().Be("databse");
        diagnostics[0].Message.Should().Contain("database");
    }

    [Fact]
    public void A_key_with_no_close_match_is_still_reported_without_a_bogus_suggestion()
    {
        var diagnostics = Validate("totallyUnrelatedSetting: 1\n");

        diagnostics.Should().ContainSingle();
        diagnostics[0].Message.Should().NotContain("did you mean");
    }

    [Fact]
    public void Malformed_yaml_is_one_error_not_an_exception()
    {
        var diagnostics = Validate("database:\n  provider: [unclosed\n");

        diagnostics.Should().ContainSingle()
            .Which.Severity.Should().Be(DiagnosticSeverity.Error);
    }

    [Fact]
    public void A_non_mapping_document_is_an_error()
    {
        var diagnostics = Validate("- just\n- a\n- list\n");

        diagnostics.Should().Contain(d => d.Severity == DiagnosticSeverity.Error
            && d.Message.Contains("must be a mapping"));
    }

    [Theory]
    [InlineData("notifications:\n  webhookUrl: not-a-url\n", "absolute http(s) URL")]
    [InlineData("notifications:\n  dedupeSeconds: -1\n", "cannot be negative")]
    [InlineData("ai:\n  embeddingDimensions: 0\n", "greater than 0")]
    [InlineData("ai:\n  limits:\n    maxCallsPerRun: -5\n", "cannot be negative")]
    public void Invalid_values_are_errors(string yaml, string expected)
    {
        var diagnostics = Validate(yaml);

        diagnostics.Should().Contain(d => d.Severity == DiagnosticSeverity.Error
            && d.Message.Contains(expected));
    }

    [Fact]
    public void An_unknown_notification_event_warns_with_the_known_set()
    {
        var diagnostics = Validate("""
            notifications:
              events: [new-failures]
            """);

        diagnostics.Should().Contain(d => d.Severity == DiagnosticSeverity.Warning
            && d.Message.Contains("new-failure"));
    }

    [Fact]
    public void An_incomplete_smtp_block_is_an_error_because_it_would_silently_never_send()
    {
        var diagnostics = Validate("""
            notifications:
              smtp:
                host: smtp.example.com
            """);

        diagnostics.Should().Contain(d => d.Severity == DiagnosticSeverity.Error && d.Path.EndsWith("from"));
        diagnostics.Should().Contain(d => d.Severity == DiagnosticSeverity.Error && d.Path.EndsWith("to"));
    }

    [Fact]
    public void A_github_repo_without_a_slash_is_an_error()
    {
        var diagnostics = Validate("""
            notifications:
              gitHub:
                repo: justaname
            """);

        diagnostics.Should().Contain(d => d.Severity == DiagnosticSeverity.Error
            && d.Message.Contains("owner/name"));
    }

    [Fact]
    public void An_unavailable_database_provider_is_only_a_warning()
    {
        // The same file is correct when run under the Docker/full build, so refusing to start
        // would be wrong.
        var diagnostics = Validate("""
            database:
              provider: postgres
            """);

        diagnostics.Should().OnlyContain(d => d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void The_known_key_schema_covers_every_settable_config_property()
    {
        // Guards the reflection-derived schema against a future config type that the walker
        // would treat as an unknown key.
        var yaml = """
            database:
              provider: sqlite
              connectionString: x
            ai:
              provider: ollama
              model: m
              baseUrl: http://localhost:11434
              apiKeyEnv: X
              embeddings: false
              embeddingModel: e
              embeddingDimensions: 8
              limits:
                maxCallsPerRun: 0
                maxCallsPerMinute: 0
                maxRequestChars: 0
            notifications:
              events: []
              slackWebhookUrl:
              webhookUrl:
              discordWebhookUrl:
              teamsWebhookUrl:
              dedupeSeconds: 300
              smtp:
                host: h
                port: 587
                useSsl: true
                username: u
                passwordEnv: P
                from: a@b.c
                to: d@e.f
              gitHub:
                repo: o/n
                tokenEnv: T
                labels: [cifail]
                apiBaseUrl:
            """;

        Validate(yaml).Should().NotContain(d => d.Message.Contains("unknown setting"));
    }
}

/// <summary>Covers <see cref="ConfigException"/>'s job: locating the problem for the user.</summary>
public sealed class ConfigExceptionTests
{
    [Fact]
    public void Load_of_malformed_yaml_throws_with_the_path_and_position()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cifail-config-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.yaml");
        File.WriteAllText(path, "database:\n  provider: [unclosed\n");

        try
        {
            var load = () => ConfigLoader.LoadFile(path);

            var ex = load.Should().Throw<ConfigException>().Which;
            ex.Path.Should().Be(path);
            ex.Line.Should().BeGreaterThan(0);
            ex.Location.Should().StartWith(path);
            // The position is carried in the fields, so it must not also be duplicated into
            // the message the CLI renders after the location prefix.
            ex.Message.Should().NotStartWith("(");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
