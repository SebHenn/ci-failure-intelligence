using System.Text.Json;
using CiFail.Core.Configuration;
using CiFail.Core.Notifications;
using CiFail.Core.Notifications.Channels;
using CiFail.Core.Storage;
using FluentAssertions;
using Xunit;

namespace CiFail.Core.Tests.Notifications;

/// <summary>Payload shape + GitHub idempotency for the R27 channels, driven by fakes (no network).</summary>
public class NotificationChannelsTests
{
    private static StoredAnalysis Stored(string fingerprint = "nuget-nu1101:abc") => new()
    {
        Id = 7,
        AnalyzedAt = DateTimeOffset.UtcNow,
        Source = "build.log",
        Ecosystem = "dotnet",
        RuleId = "nuget-nu1101",
        Matched = true,
        Fingerprint = fingerprint,
        LogHash = "deadbeef",
        Excerpt = "error NU1101: Unable to find package Newtonsoft.Jsn",
        GitCommit = "abc1234",
    };

    private static Notification Note(NotificationEvent ev, string fp = "nuget-nu1101:abc") => new(ev, Stored(fp));

    [Fact]
    public void Discord_posts_content_and_an_embed_with_facts()
    {
        string? url = null, body = null;
        var notifier = new DiscordNotifier("https://discord.example/hook", (u, b) => { url = u; body = b; });

        notifier.Notify(Note(NotificationEvent.NewFailure));

        url.Should().Be("https://discord.example/hook");
        using var doc = JsonDocument.Parse(body!);
        doc.RootElement.GetProperty("content").GetString().Should().Contain("New CI failure");
        var embed = doc.RootElement.GetProperty("embeds")[0];
        embed.GetProperty("description").GetString().Should().Contain("NU1101");
        embed.GetProperty("fields").EnumerateArray()
            .Should().Contain(f => f.GetProperty("value").GetString() == "nuget-nu1101:abc");
    }

    [Fact]
    public void Teams_posts_a_message_card_with_event_theme_color()
    {
        string? body = null;
        new TeamsNotifier("https://teams.example/hook", (_, b) => body = b)
            .Notify(Note(NotificationEvent.Resolved));

        using var doc = JsonDocument.Parse(body!);
        doc.RootElement.GetProperty("@type").GetString().Should().Be("MessageCard");
        doc.RootElement.GetProperty("themeColor").GetString().Should().Be("2EB67D"); // green for resolved
        doc.RootElement.GetProperty("title").GetString().Should().Contain("resolved");
        var facts = doc.RootElement.GetProperty("sections")[0].GetProperty("facts");
        facts.EnumerateArray().Should().Contain(f => f.GetProperty("value").GetString() == "nuget-nu1101:abc");
    }

    [Fact]
    public void GitHub_opens_an_issue_with_the_fingerprint_marker_on_a_new_failure()
    {
        var client = new FakeGitHubClient();
        new GitHubIssueNotifier(client).Notify(Note(NotificationEvent.NewFailure, "fp-1"));

        client.Created.Should().ContainSingle();
        client.Created[0].Body.Should().Contain(GitHubIssueNotifier.Marker("fp-1"));
        client.Comments.Should().BeEmpty();
    }

    [Fact]
    public void GitHub_comments_instead_of_duplicating_when_the_issue_already_exists()
    {
        var client = new FakeGitHubClient();
        // Seed an existing open issue carrying this fingerprint's marker.
        client.Seed(101, GitHubIssueNotifier.Marker("fp-1"));

        new GitHubIssueNotifier(client).Notify(Note(NotificationEvent.Recurrence, "fp-1"));

        client.Created.Should().BeEmpty("the tracking issue already exists");
        client.Comments.Should().ContainSingle().Which.Number.Should().Be(101);
    }

    [Fact]
    public void GitHub_does_not_open_an_issue_for_a_resolved_event_with_no_tracking_issue()
    {
        var client = new FakeGitHubClient();
        new GitHubIssueNotifier(client).Notify(Note(NotificationEvent.Resolved, "fp-unknown"));

        client.Created.Should().BeEmpty();
        client.Comments.Should().BeEmpty();
    }

    [Fact]
    public void FromConfig_wires_discord_teams_and_github()
    {
        Environment.SetEnvironmentVariable("CIFAIL_TEST_GH_TOKEN", "tok");
        try
        {
            var dispatcher = NotificationDispatcher.FromConfig(new NotificationsConfig
            {
                DiscordWebhookUrl = "https://discord.example/hook",
                TeamsWebhookUrl = "https://teams.example/hook",
                GitHub = new GitHubNotifierConfig { Repo = "owner/repo", TokenEnv = "CIFAIL_TEST_GH_TOKEN" },
            });

            dispatcher.Should().NotBeNull();
            dispatcher!.HasNotifiers.Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("CIFAIL_TEST_GH_TOKEN", null);
        }
    }

    [Fact]
    public void GitHub_FromConfig_returns_null_without_a_token()
    {
        GitHubIssueNotifier.FromConfig(new GitHubNotifierConfig { Repo = "owner/repo", TokenEnv = "CIFAIL_MISSING_TOKEN" })
            .Should().BeNull();
    }

    private sealed class FakeGitHubClient : IGitHubIssueClient
    {
        private readonly Dictionary<int, string> _issues = new();
        private int _next = 1;

        public List<(int Number, string Title, string Body)> Created { get; } = new();
        public List<(int Number, string Body)> Comments { get; } = new();

        public void Seed(int number, string body) => _issues[number] = body;

        public int? FindOpenIssue(string marker) =>
            _issues.FirstOrDefault(kv => kv.Value.Contains(marker, StringComparison.Ordinal)).Key is var n && n != 0 ? n : null;

        public int CreateIssue(string title, string body, IReadOnlyList<string> labels)
        {
            var number = _next++;
            _issues[number] = body;
            Created.Add((number, title, body));
            return number;
        }

        public void CommentOnIssue(int number, string body) => Comments.Add((number, body));
    }
}
