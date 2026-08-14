using System.Net;
using System.Text;
using FluentAssertions;

namespace CiFail.Server.Tests;

/// <summary>
/// The dashboard's filters used to run over <c>GetRecent(200)</c> in memory, so anything older
/// than the newest 200 records was invisible to them — and the page rendered "No failures match"
/// as though it had looked. This seeds past that boundary and asserts the old record is still
/// found, which is the only way to catch the regression: with fewer than 200 rows the broken
/// implementation and the correct one are indistinguishable.
/// </summary>
public sealed class ServeDashboardPagingTests : IClassFixture<ServeFixture>, IAsyncLifetime
{
    private const int Noise = 230;
    private const string Needle =
        "error NU1101: Unable to find package Needle.Package. No packages exist with this id in source(s): nuget.org";

    private readonly HttpClient _client;

    public ServeDashboardPagingTests(ServeFixture fixture) => _client = fixture.Client;

    public async Task InitializeAsync()
    {
        // The needle goes in first, so it ends up oldest.
        var first = await _client.PostAsync("analyze?source=needle.log",
            new StringContent(Needle, Encoding.UTF8, "text/plain"));
        first.EnsureSuccessStatusCode();

        var id = System.Text.Json.JsonDocument.Parse(await first.Content.ReadAsStringAsync())
            .RootElement.GetProperty("HistoryId").GetInt64();

        await _client.PostAsync($"resolve/{id}",
            new StringContent("{\"note\":\"bumped the feed\"}", Encoding.UTF8, "application/json"));

        for (var i = 0; i < Noise; i++)
        {
            await _client.PostAsync($"analyze?source=noise-{i}.log",
                new StringContent($"Error: Process completed with exit code {i % 7 + 1}.",
                    Encoding.UTF8, "text/plain"));
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<string> Dashboard(string query)
    {
        var response = await _client.GetAsync(query);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task A_resolved_failure_older_than_the_old_window_is_still_found()
    {
        var html = await Dashboard("?status=resolved");

        html.Should().Contain("needle.log");
        html.Should().NotContain("No failures match");
    }

    [Fact]
    public async Task Free_text_search_reaches_beyond_one_page()
    {
        var html = await Dashboard("?q=needle");

        html.Should().Contain("needle.log");
    }

    [Fact]
    public async Task The_ecosystem_dropdown_offers_an_ecosystem_that_only_appears_in_old_rows()
    {
        // The needle is the only dotnet record and it is buried; the dropdown used to be built
        // from the same 200-row window as the table, so it disappeared from the filter entirely.
        var html = await Dashboard("");

        html.Should().Contain("dotnet");
    }

    [Fact]
    public async Task Paging_is_offered_and_moves_to_older_records()
    {
        var firstPage = await Dashboard("");
        firstPage.Should().Contain("older");

        var secondPage = await Dashboard("?page=2");
        secondPage.Should().Contain("page 2 of");
    }

    /// <summary>Still no JavaScript — the pager is plain links (R28's invariant).</summary>
    [Fact]
    public async Task The_pager_adds_no_script()
    {
        var html = await Dashboard("?page=2");

        html.Should().NotContain("<script");
    }
}
