using System.Text.Json;

namespace CiFail.Core.Notifications.Channels;

/// <summary>
/// Slack channel via an incoming-webhook URL: POSTs a simple <c>{ "text": "…" }</c> message.
/// </summary>
public sealed class SlackNotifier : INotifier
{
    private readonly string _webhookUrl;

    public SlackNotifier(string webhookUrl) => _webhookUrl = webhookUrl;

    public string Name => "slack";

    public void Notify(Notification n)
    {
        var a = n.Analysis;
        var excerpt = a.Excerpt.Length > 300 ? a.Excerpt[..300] + "…" : a.Excerpt;
        var text = $"*{n.Title}*\n`{a.Fingerprint}` · {a.Ecosystem} · {a.Source}\n{excerpt}";
        NotifierHttp.PostJson(_webhookUrl, JsonSerializer.Serialize(new { text }));
    }
}
