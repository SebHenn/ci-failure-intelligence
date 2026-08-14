using System.Net;
using FluentAssertions;

namespace CiFail.Server.Tests;

/// <summary>
/// The sign-in route is necessarily public — a browser signing in has no cookie yet — and it
/// compares a submitted string against the server token. Without a limiter that token can be
/// guessed at whatever rate the network allows, which for a loopback or in-cluster attacker is
/// thousands of attempts a second.
///
/// <para>
/// Uses its own fixture instance (xUnit builds one per test class), so the permits this test
/// burns cannot make <see cref="ServeLoginTests"/> flaky — they run against a different server
/// with its own limiter.
/// </para>
/// </summary>
public sealed class ServeLoginRateLimitTests : IClassFixture<AuthServeFixture>
{
    private readonly HttpClient _client;

    public ServeLoginRateLimitTests(AuthServeFixture fixture) => _client = fixture.Client;

    [Fact]
    public async Task Repeated_wrong_tokens_start_getting_429()
    {
        var statuses = new List<HttpStatusCode>();

        // The window allows 10 per minute; 15 attempts must run into it.
        for (var i = 0; i < 15; i++)
        {
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["token"] = $"definitely-wrong-{i}",
            });
            var response = await _client.PostAsync("ui/login", form);
            statuses.Add(response.StatusCode);
        }

        statuses.Should().Contain(HttpStatusCode.TooManyRequests,
            "an unthrottled sign-in route lets the server token be brute-forced");

        // The early attempts still got a real answer — the limiter throttles, it doesn't break
        // sign-in. (A wrong token redirects to /login?error=1, which HttpClient follows, so what
        // lands here is the 200 from the sign-in page.)
        statuses.Take(5).Should().NotContain(HttpStatusCode.TooManyRequests);
    }
}
