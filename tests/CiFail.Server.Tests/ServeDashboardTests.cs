using System.Net;
using FluentAssertions;

namespace CiFail.Server.Tests;

/// <summary>The bundled web dashboard (R12): served at / from an embedded resource, and public
/// (no token) so users can load it and enter their token in-page.</summary>
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
}
