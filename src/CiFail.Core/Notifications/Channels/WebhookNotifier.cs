using System.Text;
using System.Text.Json;

namespace CiFail.Core.Notifications.Channels;

/// <summary>
/// Generic webhook channel: POSTs a small JSON document describing the event to a URL. Raw
/// <see cref="HttpClient"/> (no SDK), short timeout so it never holds up a response for long.
/// </summary>
public sealed class WebhookNotifier : INotifier
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    private readonly string _url;

    public WebhookNotifier(string url) => _url = url;

    public string Name => "webhook";

    public void Notify(Notification n)
    {
        var a = n.Analysis;
        var payload = new
        {
            @event = n.EventKey,
            title = n.Title,
            id = a.Id,
            fingerprint = a.Fingerprint,
            ruleId = a.RuleId,
            ecosystem = a.Ecosystem,
            source = a.Source,
            excerpt = a.Excerpt,
            status = a.Status,
            resolution = a.Resolution,
            resolutionSource = a.ResolutionSource,
            repoId = a.RepoId,
            gitCommit = a.GitCommit,
            gitBranch = a.GitBranch,
            analyzedAt = a.AnalyzedAt,
        };
        NotifierHttp.PostJson(_url, JsonSerializer.Serialize(payload, Json));
    }
}
