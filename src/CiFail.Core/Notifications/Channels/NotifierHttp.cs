using System.Text;

namespace CiFail.Core.Notifications.Channels;

/// <summary>Tiny shared HTTP helper for webhook-style notifiers (Slack, generic webhook).</summary>
internal static class NotifierHttp
{
    /// <summary>POST a JSON body to <paramref name="url"/> with a short timeout, throwing on non-2xx.</summary>
    public static void PostJson(string url, string json)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        using var response = client.Send(request);
        response.EnsureSuccessStatusCode();
    }
}
