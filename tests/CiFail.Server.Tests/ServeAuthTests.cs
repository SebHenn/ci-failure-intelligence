using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CiFail.Core.Storage;
using FluentAssertions;

namespace CiFail.Server.Tests;

/// <summary>Auth middleware (R9): every route except /healthz needs a valid bearer token.</summary>
public sealed class ServeAuthTests : IClassFixture<AuthServeFixture>
{
    private const string SomeLog =
        "error NU1101: Unable to find package Foo. No packages exist with this id in source(s): nuget.org";

    private readonly AuthServeFixture _fixture;
    private readonly HttpClient _client;

    public ServeAuthTests(AuthServeFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Fact]
    public async Task Healthz_is_open_without_a_token()
    {
        var response = await _client.GetAsync("healthz");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Login_page_is_open_without_a_token()
    {
        // The sign-in page must load pre-auth so the user can submit a token (R28).
        var response = await _client.GetAsync("login");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Neither observability route is public. `/metrics` would otherwise leak rule ids,
    /// ecosystems and failure counts to anyone who can reach the pod — Prometheus supports a
    /// bearer token in the scrape config, so there is no reason to open it.
    /// </summary>
    [Theory]
    [InlineData("metrics")]
    [InlineData("openapi.json")]
    public async Task Observability_routes_require_a_token(string route)
    {
        var response = await _client.GetAsync(route);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Dashboard_requires_auth_when_a_token_is_set()
    {
        // The dashboard now renders data server-side, so it's protected — a tokenless API-style
        // request (no text/html Accept, no cookie) gets 401 just like any other protected route.
        var response = await _client.GetAsync("");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Protected_route_without_a_token_is_401()
    {
        var response = await _client.GetAsync("history");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().Should().Contain("Bearer");
    }

    [Fact]
    public async Task Protected_route_with_a_wrong_token_is_401()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "history");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-the-token");
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Protected_route_with_the_right_token_succeeds()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "history");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AuthServeFixture.Token);
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public void HttpAnalysisStore_sends_the_token_and_can_read_history()
    {
        // Seed a record through the authed analyze endpoint.
        using (var seed = new HttpRequestMessage(HttpMethod.Post, "analyze?source=auth.log")
        {
            Content = new StringContent(SomeLog, Encoding.UTF8, "text/plain"),
        })
        {
            seed.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AuthServeFixture.Token);
            _client.Send(seed).EnsureSuccessStatusCode();
        }

        using var store = new HttpAnalysisStore(_fixture.BaseUrl, AuthServeFixture.Token);
        store.GetRecent(10).Should().NotBeEmpty();
    }

    [Fact]
    public void HttpAnalysisStore_without_a_token_is_rejected()
    {
        using var store = new HttpAnalysisStore(_fixture.BaseUrl); // no token
        var act = () => store.GetRecent(10);
        act.Should().Throw<HttpRequestException>();
    }
}
