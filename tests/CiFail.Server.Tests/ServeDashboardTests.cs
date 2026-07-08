using System.Net;
using FluentAssertions;

namespace CiFail.Server.Tests;

/// <summary>The C#-rendered dashboard (R28, Blazor static SSR): served at / and /index.html.
/// In open mode (no token) it renders directly, like the old embedded page did.</summary>
public sealed class ServeDashboardTests : IClassFixture<ServeFixture>
{
    private readonly HttpClient _client;

    public ServeDashboardTests(ServeFixture fixture) => _client = fixture.Client;

    [Fact]
    public async Task Root_serves_the_dashboard_html()
    {
        var response = await _client.GetAsync("");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("<!DOCTYPE html>");
        body.Should().Contain("cifail");
    }

    [Fact]
    public async Task Index_html_path_also_serves_the_dashboard()
    {
        var response = await _client.GetAsync("index.html");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("<!DOCTYPE html>");
    }

    [Fact]
    public async Task Login_page_is_served()
    {
        var response = await _client.GetAsync("login");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("access token");
    }
}
