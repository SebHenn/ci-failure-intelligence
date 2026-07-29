using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;

namespace CiFail.Server.Tests;

/// <summary>The R28 browser sign-in flow: POST /login validates the token, sets an auth cookie,
/// and the dashboard then renders; a browser GET without the cookie is bounced to /login.</summary>
public sealed class ServeLoginTests : IClassFixture<AuthServeFixture>
{
    private readonly AuthServeFixture _fixture;

    public ServeLoginTests(AuthServeFixture fixture) => _fixture = fixture;

    private HttpClient NewBrowser()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new() };
        return new HttpClient(handler) { BaseAddress = new Uri(_fixture.BaseUrl.TrimEnd('/') + "/") };
    }

    [Fact]
    public async Task A_browser_get_without_a_cookie_is_redirected_to_login()
    {
        using var browser = NewBrowser();
        using var request = new HttpRequestMessage(HttpMethod.Get, "");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        var response = await browser.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Contain("/login");
    }

    [Fact]
    public async Task Posting_the_right_token_sets_a_cookie_that_unlocks_the_dashboard()
    {
        using var browser = NewBrowser();

        var login = await browser.PostAsync("ui/login", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["token"] = AuthServeFixture.Token }));

        login.StatusCode.Should().Be(HttpStatusCode.Redirect);
        login.Headers.Location!.ToString().Should().Be("/");
        login.Headers.GetValues("Set-Cookie").Should().Contain(c => c.StartsWith("cifail_auth="));

        // The handler's cookie container now carries the auth cookie; the dashboard renders.
        var dash = await browser.GetAsync("");
        dash.StatusCode.Should().Be(HttpStatusCode.OK);
        (await dash.Content.ReadAsStringAsync()).Should().Contain("<!DOCTYPE html>");
    }

    [Fact]
    public async Task Posting_a_wrong_token_is_rejected_back_to_login()
    {
        using var browser = NewBrowser();

        var login = await browser.PostAsync("ui/login", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["token"] = "nope" }));

        login.StatusCode.Should().Be(HttpStatusCode.Redirect);
        login.Headers.Location!.ToString().Should().Contain("/login?error=1");
    }

    private static StringContent NotAForm() =>
        new("{\"token\":\"x\"}", System.Text.Encoding.UTF8, "application/json");

    // /ui/login is public, so a malformed post from anyone must not become a bare 500.
    [Fact]
    public async Task A_non_form_post_to_login_is_a_bad_request_not_a_server_error()
    {
        using var browser = NewBrowser();

        var response = await browser.PostAsync("ui/login", NotAForm());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_non_form_post_to_resolve_is_a_bad_request_not_a_server_error()
    {
        using var browser = NewBrowser();
        // /ui/resolve is behind auth, so sign in first — otherwise this only proves the 401.
        await browser.PostAsync("ui/login", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["token"] = AuthServeFixture.Token }));

        var response = await browser.PostAsync("ui/resolve", NotAForm());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
