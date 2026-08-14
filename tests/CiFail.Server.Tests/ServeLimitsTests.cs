using System.Net;
using System.Text;
using FluentAssertions;

namespace CiFail.Server.Tests;

/// <summary>
/// Bounds on what a single request can ask the server to do.
///
/// <para>
/// Every one of these was unbounded: <c>/analyze</c> materialized any body as one string (and then
/// normalized, scrubbed and tokenized it, several full-size copies deep), <c>/history?limit=</c>
/// went straight into <c>LIMIT $limit</c>, and <c>/repos/{id}/open</c> had no limit at all. None of
/// them needed authentication to reach on a server started without a token.
/// </para>
/// </summary>
public sealed class ServeLimitsTests : IClassFixture<ServeFixture>
{
    private const string MatchingLog =
        "error NU1101: Unable to find package Newtonsoft.Jsn. No packages exist with this id in source(s): nuget.org";

    private readonly HttpClient _client;

    public ServeLimitsTests(ServeFixture fixture) => _client = fixture.Client;

    [Fact]
    public async Task A_log_over_the_size_limit_is_refused_with_413()
    {
        // The fixture uses the default 10 MB ceiling.
        var oversized = new string('x', 11 * 1024 * 1024);

        var response = await _client.PostAsync("analyze?source=huge.log",
            new StringContent(oversized, Encoding.UTF8, "text/plain"));

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task A_log_within_the_limit_is_still_analyzed()
    {
        var response = await _client.PostAsync("analyze?source=fine.log",
            new StringContent(MatchingLog, Encoding.UTF8, "text/plain"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("nuget-nu1101");
    }

    /// <summary>
    /// A caller asking for two billion rows must not get two billion rows. The clamp is silent by
    /// design — the request is served, just not at the size asked for.
    /// </summary>
    [Fact]
    public async Task History_limit_is_clamped_rather_than_honoured()
    {
        for (var i = 0; i < 3; i++)
        {
            await _client.PostAsync($"analyze?source=row{i}.log",
                new StringContent(MatchingLog + i, Encoding.UTF8, "text/plain"));
        }

        var response = await _client.GetAsync("history?limit=2000000000");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().StartWith("[");
    }

    [Fact]
    public async Task Open_failures_accepts_a_limit()
    {
        var response = await _client.GetAsync("repos/anything/open?limit=1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Liveness must not depend on the store — restarting a healthy pod because the database
    /// blinked turns a database problem into an outage. Readiness must, or the server keeps
    /// taking traffic it can only fail.
    /// </summary>
    [Fact]
    public async Task Healthz_and_readyz_both_answer_when_the_store_is_fine()
    {
        (await _client.GetAsync("healthz")).StatusCode.Should().Be(HttpStatusCode.OK);

        var ready = await _client.GetAsync("readyz");
        ready.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ready.Content.ReadAsStringAsync()).Should().Contain("ready");
    }
}

/// <summary>
/// The probes have to work on a server that requires a token: the kubelet does not send one, so
/// a probe route behind auth means the pod never becomes ready.
/// </summary>
public sealed class ServeProbeAuthTests : IClassFixture<AuthServeFixture>
{
    private readonly HttpClient _client;

    public ServeProbeAuthTests(AuthServeFixture fixture) => _client = fixture.Client;

    [Theory]
    [InlineData("healthz")]
    [InlineData("readyz")]
    public async Task Probes_answer_without_a_token(string path)
    {
        var response = await _client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
