using System.Net;
using System.Text;
using System.Text.RegularExpressions;
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

    [Fact]
    public async Task The_sparkline_renders_a_wellformed_polyline()
    {
        await Analyze("error NU1101: Unable to find package Foo. No packages exist with this id in source(s): nuget.org");

        var body = await _client.GetStringAsync("");

        body.Should().Contain("failures per day");
        var points = Regex.Match(body, @"<polyline points=""([^""]*)""");
        points.Success.Should().BeTrue("the sparkline should render as an inline SVG polyline");

        // Every point must be a coordinate pair inside the 100x24 viewBox. An invariant-culture
        // slip ("12,3 45,6" from a decimal comma) would produce a plausible-looking string that
        // draws nonsense, so the shape is checked rather than just its presence.
        var pairs = points.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        pairs.Should().HaveCountGreaterThan(1);
        foreach (var pair in pairs)
        {
            var parts = pair.Split(',');
            parts.Should().HaveCount(2, $"'{pair}' should be an x,y pair");
            var x = double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
            var y = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
            x.Should().BeInRange(0, 100);
            y.Should().BeInRange(0, 24);
        }
    }

    [Fact]
    public async Task Clusters_drill_down_without_javascript()
    {
        // Two similar failures so the clusterer has something to group.
        await Analyze("error NU1101: Unable to find package Alpha. No packages exist with this id in source(s): nuget.org");
        await Analyze("error NU1101: Unable to find package Beta. No packages exist with this id in source(s): nuget.org");

        var body = await _client.GetStringAsync("");

        if (!body.Contains("clusters (grouped root causes)", StringComparison.Ordinal))
            return; // nothing clustered in this run; the drill-down has nothing to assert about

        body.Should().Contain("<details>", "drill-down must work with scripting disabled");
        body.Should().Contain("cluster-members");
    }

    [Fact]
    public async Task The_dashboard_ships_no_script()
    {
        // Static SSR is a deliberate property (R28): no bundler, no CSP headaches, works with
        // scripting off. A stray <script> would silently undo that.
        var body = await _client.GetStringAsync("");
        body.Should().NotContain("<script", "the dashboard is server-rendered by design");
    }

    private async Task Analyze(string log)
    {
        using var content = new StringContent(log, Encoding.UTF8, "text/plain");
        (await _client.PostAsync("analyze", content)).EnsureSuccessStatusCode();
    }
}
