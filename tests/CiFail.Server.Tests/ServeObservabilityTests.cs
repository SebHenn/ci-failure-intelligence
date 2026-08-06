using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace CiFail.Server.Tests;

/// <summary>
/// <c>GET /metrics</c> and <c>GET /openapi.json</c> against a real running server.
/// </summary>
public sealed class ServeObservabilityTests : IClassFixture<ServeFixture>
{
    private const string MatchingLog =
        "error NU1101: Unable to find package Newtonsoft.Jsn. No packages exist with this id in source(s): nuget.org";

    private readonly HttpClient _client;

    public ServeObservabilityTests(ServeFixture fixture) => _client = fixture.Client;

    [Fact]
    public async Task Metrics_are_served_in_the_format_prometheus_expects()
    {
        var response = await _client.GetAsync("metrics");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        // Prometheus content-negotiates on this exact string; getting it wrong makes a scrape
        // fail in a way that looks like the endpoint is down.
        response.Content.Headers.ContentType!.ToString()
            .Should().StartWith("text/plain").And.Contain("version=0.0.4");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("# HELP cifail_failures_total");
        body.Should().Contain("# TYPE cifail_failures_total gauge");
        body.Should().MatchRegex(@"(?m)^cifail_failures_total \d+$");
    }

    [Fact]
    public async Task Metrics_count_an_analyzed_failure()
    {
        var before = ReadGauge(await _client.GetStringAsync("metrics"), "cifail_failures_total");

        await Analyze(MatchingLog);

        var after = ReadGauge(await _client.GetStringAsync("metrics"), "cifail_failures_total");
        after.Should().Be(before + 1, "the gauge reads from the same history the API writes to");
    }

    [Fact]
    public async Task Metrics_label_values_are_escaped()
    {
        await Analyze(MatchingLog);
        var body = await _client.GetStringAsync("metrics");

        // Every labelled sample must have balanced quotes; an unescaped value would produce a
        // line Prometheus rejects, taking the whole scrape down rather than one series.
        foreach (var line in body.Split('\n').Where(l => l.Contains('{') && !l.StartsWith('#')))
        {
            var labels = line[(line.IndexOf('{') + 1)..line.LastIndexOf('}')];
            CountUnescapedQuotes(labels).Should().Be(0,
                $"labels should be well-formed, but got: {line}");
        }
    }

    [Fact]
    public async Task Openapi_document_is_valid_json_and_describes_the_real_routes()
    {
        var response = await _client.GetAsync("openapi.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("openapi").GetString().Should().StartWith("3.");

        var paths = doc.RootElement.GetProperty("paths");
        foreach (var route in new[] { "/healthz", "/analyze", "/history", "/stats", "/clusters", "/metrics" })
            paths.TryGetProperty(route, out _).Should().BeTrue($"{route} should be documented");
    }

    [Fact]
    public async Task Every_documented_route_actually_answers()
    {
        // The document is hand-written, so nothing but a test stops it describing a route that
        // was renamed or removed. Only GET routes with no path parameters are checked — the
        // point is to catch drift, not to exercise the API.
        using var doc = JsonDocument.Parse(await _client.GetStringAsync("openapi.json"));

        foreach (var path in doc.RootElement.GetProperty("paths").EnumerateObject())
        {
            if (path.Name.Contains('{') || !path.Value.TryGetProperty("get", out _)) continue;

            var response = await _client.GetAsync(path.Name.TrimStart('/'));
            response.StatusCode.Should().NotBe(HttpStatusCode.NotFound,
                $"{path.Name} is documented but the server does not serve it");
        }
    }

    private async Task Analyze(string log)
    {
        using var content = new StringContent(log, Encoding.UTF8, "text/plain");
        (await _client.PostAsync("analyze", content)).EnsureSuccessStatusCode();
    }

    private static double ReadGauge(string exposition, string metric)
    {
        var line = exposition.Split('\n')
            .First(l => l.StartsWith(metric + " ", StringComparison.Ordinal));
        return double.Parse(line[(metric.Length + 1)..].Trim(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Quotes that aren't preceded by a backslash must come in pairs.</summary>
    private static int CountUnescapedQuotes(string labels)
    {
        int count = 0;
        for (int i = 0; i < labels.Length; i++)
            if (labels[i] == '"' && (i == 0 || labels[i - 1] != '\\'))
                count++;
        return count % 2;
    }
}
