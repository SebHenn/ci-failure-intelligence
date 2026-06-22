using System.Net;
using System.Net.Http.Headers;
using CiFail.Server;
using FluentAssertions;

namespace CiFail.Server.Tests;

/// <summary>Per-client named tokens (R20): any configured token works; an unknown one is rejected.</summary>
public sealed class ServeMultiTokenTests : IClassFixture<MultiTokenServeFixture>
{
    private readonly HttpClient _client;

    public ServeMultiTokenTests(MultiTokenServeFixture fixture) => _client = fixture.Client;

    [Theory]
    [InlineData(MultiTokenServeFixture.TokenA)]
    [InlineData(MultiTokenServeFixture.TokenB)]
    public async Task Each_configured_token_is_accepted(string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "history");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_revoked_or_unknown_token_is_401()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "history");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "client-c-was-revoked");
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public void ParseList_auto_names_bare_tokens()
    {
        var tokens = ServerTokens.ParseList(" a , b ,, c ");
        tokens.Select(t => t.Token).Should().Equal("a", "b", "c");
        tokens.Select(t => t.Name).Should().Equal("token1", "token2", "token3");
    }

    [Fact]
    public void ParseFile_reads_token_then_optional_name_and_skips_comments()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(path, new[]
            {
                "# team tokens",
                "abc123== ci-runner",   // base64 padding in the token, name after
                "  ",
                "plain-token",          // no name -> auto-named
            });

            var tokens = ServerTokens.ParseFile(path);

            tokens.Should().HaveCount(2);
            tokens[0].Should().Be(new NamedToken("ci-runner", "abc123=="));
            tokens[1].Token.Should().Be("plain-token");
        }
        finally { File.Delete(path); }
    }
}
