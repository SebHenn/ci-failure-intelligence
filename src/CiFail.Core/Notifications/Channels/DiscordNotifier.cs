using System.Text.Json;

namespace CiFail.Core.Notifications.Channels;

/// <summary>
/// Discord channel via an incoming-webhook URL. POSTs a <c>{ content, embeds:[…] }</c> message — a
/// short summary line plus an embed carrying the fingerprint/ecosystem/source and a log excerpt.
/// </summary>
public sealed class DiscordNotifier : INotifier
{
    // Discord rejects an empty message and caps content at 2000 / an embed description at 4096.
    private const int ContentLimit = 280;
    private const int DescriptionLimit = 1500;

    private readonly string _webhookUrl;
    private readonly Action<string, string> _post;

    public DiscordNotifier(string webhookUrl) : this(webhookUrl, NotifierHttp.PostJson) { }

    /// <summary>Test seam: inject the poster so payload shape can be asserted without a network call.</summary>
    internal DiscordNotifier(string webhookUrl, Action<string, string> post)
    {
        _webhookUrl = webhookUrl;
        _post = post;
    }

    public string Name => "discord";

    public void Notify(Notification n)
    {
        var a = n.Analysis;
        var description = a.Excerpt.Length > DescriptionLimit ? a.Excerpt[..DescriptionLimit] + "…" : a.Excerpt;
        var payload = new
        {
            content = Truncate(n.Title, ContentLimit),
            embeds = new[]
            {
                new
                {
                    title = Truncate(n.Title, 256),
                    description,
                    fields = new[]
                    {
                        new { name = "fingerprint", value = a.Fingerprint, inline = true },
                        new { name = "ecosystem", value = string.IsNullOrWhiteSpace(a.Ecosystem) ? "unknown" : a.Ecosystem, inline = true },
                        new { name = "source", value = string.IsNullOrWhiteSpace(a.Source) ? "—" : a.Source, inline = false },
                    },
                },
            },
        };
        _post(_webhookUrl, JsonSerializer.Serialize(payload));
    }

    private static string Truncate(string s, int max) => s.Length > max ? s[..(max - 1)] + "…" : s;
}
