using System.Text.Json;

namespace CiFail.Core.Notifications.Channels;

/// <summary>
/// Microsoft Teams channel via an incoming-webhook URL. POSTs a legacy MessageCard (the format an
/// Office 365 connector webhook accepts) with the title, a coloured theme per event, and the
/// fingerprint/ecosystem/source/excerpt as facts.
/// </summary>
public sealed class TeamsNotifier : INotifier
{
    private const int ExcerptLimit = 1500;

    private readonly string _webhookUrl;
    private readonly Action<string, string> _post;

    public TeamsNotifier(string webhookUrl) : this(webhookUrl, NotifierHttp.PostJson) { }

    /// <summary>Test seam: inject the poster so payload shape can be asserted without a network call.</summary>
    internal TeamsNotifier(string webhookUrl, Action<string, string> post)
    {
        _webhookUrl = webhookUrl;
        _post = post;
    }

    public string Name => "teams";

    public void Notify(Notification n)
    {
        var a = n.Analysis;
        var excerpt = a.Excerpt.Length > ExcerptLimit ? a.Excerpt[..ExcerptLimit] + "…" : a.Excerpt;
        var payload = new Dictionary<string, object?>
        {
            ["@type"] = "MessageCard",
            ["@context"] = "http://schema.org/extensions",
            ["summary"] = n.Title,
            ["themeColor"] = ThemeColor(n.Event),
            ["title"] = n.Title,
            ["sections"] = new[]
            {
                new
                {
                    facts = new[]
                    {
                        new { name = "Fingerprint", value = a.Fingerprint },
                        new { name = "Ecosystem", value = string.IsNullOrWhiteSpace(a.Ecosystem) ? "unknown" : a.Ecosystem },
                        new { name = "Source", value = string.IsNullOrWhiteSpace(a.Source) ? "—" : a.Source },
                    },
                    text = excerpt,
                },
            },
        };
        _post(_webhookUrl, JsonSerializer.Serialize(payload));
    }

    private static string ThemeColor(NotificationEvent ev) => ev switch
    {
        NotificationEvent.Resolved => "2EB67D",   // green
        NotificationEvent.Recurrence => "E01E5A", // red
        _ => "ECB22E",                            // amber for a new failure
    };
}
