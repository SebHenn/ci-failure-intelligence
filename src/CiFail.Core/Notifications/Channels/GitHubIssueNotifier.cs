using CiFail.Core.Configuration;

namespace CiFail.Core.Notifications.Channels;

/// <summary>
/// Minimal GitHub issues API the notifier needs, abstracted so the channel is unit-testable without
/// a network call (mirrors how the channels inject their poster).
/// </summary>
public interface IGitHubIssueClient
{
    /// <summary>The number of an open issue whose body contains <paramref name="marker"/>, or null.</summary>
    int? FindOpenIssue(string marker);

    /// <summary>Open a new issue; returns its number.</summary>
    int CreateIssue(string title, string body, IReadOnlyList<string> labels);

    /// <summary>Add a comment to an existing issue.</summary>
    void CommentOnIssue(int number, string body);
}

/// <summary>
/// Opens (and on recurrence, comments on) a GitHub issue per distinct failure. Idempotent via a
/// hidden <c>&lt;!-- cifail:&lt;fingerprint&gt; --&gt;</c> marker in the issue body — the same scheme
/// the R19 MR/PR comment uses — so a recurring failure updates its existing issue instead of opening
/// a duplicate. <see cref="NotificationEvent.Resolved"/> closes nothing automatically (manual review),
/// it just comments. Fires only for <see cref="NotificationEvent.NewFailure"/> /
/// <see cref="NotificationEvent.Recurrence"/> / <see cref="NotificationEvent.Resolved"/>.
/// </summary>
public sealed class GitHubIssueNotifier : INotifier
{
    private readonly IGitHubIssueClient _client;
    private readonly IReadOnlyList<string> _labels;

    public GitHubIssueNotifier(IGitHubIssueClient client, IReadOnlyList<string>? labels = null)
    {
        _client = client;
        _labels = labels ?? new[] { "cifail" };
    }

    public string Name => "github-issue";

    public void Notify(Notification n)
    {
        var a = n.Analysis;
        var marker = Marker(a.Fingerprint);
        var existing = _client.FindOpenIssue(marker);

        if (existing is { } number)
        {
            // Already tracked — add a note rather than open a duplicate.
            _client.CommentOnIssue(number, $"{n.Title}\n\n{Body(n)}");
            return;
        }

        // No issue yet. Only open one for an actual failure; a stray "resolved" with no tracking
        // issue has nothing to act on.
        if (n.Event == NotificationEvent.Resolved) return;

        var body = $"{Body(n)}\n\n{marker}";
        _client.CreateIssue(n.Title, body, _labels);
    }

    /// <summary>The hidden idempotency marker for a fingerprint.</summary>
    public static string Marker(string fingerprint) => $"<!-- cifail:{fingerprint} -->";

    private static string Body(Notification n)
    {
        var a = n.Analysis;
        var excerpt = a.Excerpt.Length > 1500 ? a.Excerpt[..1500] + "…" : a.Excerpt;
        return
            $"**{n.Title}**\n\n" +
            $"- **Fingerprint:** `{a.Fingerprint}`\n" +
            $"- **Rule:** `{a.RuleId}`\n" +
            $"- **Ecosystem:** {(string.IsNullOrWhiteSpace(a.Ecosystem) ? "unknown" : a.Ecosystem)}\n" +
            $"- **Source:** {(string.IsNullOrWhiteSpace(a.Source) ? "—" : a.Source)}\n" +
            (string.IsNullOrWhiteSpace(a.GitCommit) ? "" : $"- **Commit:** `{a.GitCommit}`\n") +
            $"\n```\n{excerpt}\n```";
    }

    /// <summary>Build the configured notifier, or null when the GitHub block is incomplete.</summary>
    public static GitHubIssueNotifier? FromConfig(GitHubNotifierConfig? config)
    {
        if (config is null || string.IsNullOrWhiteSpace(config.Repo)) return null;

        var token = Environment.GetEnvironmentVariable(
            string.IsNullOrWhiteSpace(config.TokenEnv) ? "GITHUB_TOKEN" : config.TokenEnv);
        if (string.IsNullOrWhiteSpace(token)) return null;

        var client = new HttpGitHubIssueClient(config.Repo, token, config.ApiBaseUrl, config.Labels);
        return new GitHubIssueNotifier(client, config.Labels.Count > 0 ? config.Labels : null);
    }
}
