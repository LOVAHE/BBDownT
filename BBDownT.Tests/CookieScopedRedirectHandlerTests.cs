using System.Net;
using BBDownT.Core;
using BBDownT.Core.Util;

namespace BBDownT.Tests;

public class CookieScopedRedirectHandlerTests
{
    [Fact]
    public async Task Cookie_IsDroppedOnceARedirectLeavesTheTrustedHosts()
    {
        using var cookie = new CookieScope();
        var transport = new RedirectingTransport(new()
        {
            ["https://www.bilibili.com/start"] = "https://api.bilibili.com/next",
            ["https://api.bilibili.com/next"] = "https://collector.example.test/steal",
            ["https://collector.example.test/steal"] = "https://www.bilibili.com/back"
        });
        using var client = new HttpClient(new CookieScopedRedirectHandler(transport));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.bilibili.com/start");
        request.Headers.TryAddWithoutValidation("Cookie", "SESSDATA=secret");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("https://www.bilibili.com/back", response.RequestMessage!.RequestUri!.AbsoluteUri);
        Assert.Equal([true, true, false, false], transport.Requests.Select(sent => sent.HadCookie));
    }

    [Fact]
    public async Task DowngradeToHttp_IsNotFollowed()
    {
        var transport = new RedirectingTransport(new() { ["https://www.bilibili.com/start"] = "http://www.bilibili.com/plain" });
        using var client = new HttpClient(new CookieScopedRedirectHandler(transport));

        using var response = await client.GetAsync("https://www.bilibili.com/start");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task SeeOther_TurnsAPostIntoAGetWithoutBody()
    {
        var transport = new RedirectingTransport(new() { ["https://www.bilibili.com/form"] = "/done" }, HttpStatusCode.SeeOther);
        using var client = new HttpClient(new CookieScopedRedirectHandler(transport));

        using var response = await client.PostAsync("https://www.bilibili.com/form", new ByteArrayContent([1, 2, 3]));

        Assert.Equal("https://www.bilibili.com/done", response.RequestMessage!.RequestUri!.AbsoluteUri);
        Assert.Equal([(HttpMethod.Post, true), (HttpMethod.Get, false)], transport.Requests.Select(sent => (sent.Method, sent.HadBody)));
    }

    private sealed class RedirectingTransport(Dictionary<string, string> redirects, HttpStatusCode status = HttpStatusCode.Found)
        : HttpMessageHandler
    {
        internal List<(HttpMethod Method, bool HadCookie, bool HadBody)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.Headers.Contains("Cookie"), request.Content is not null));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
            if (redirects.TryGetValue(request.RequestUri!.AbsoluteUri, out var location))
            {
                response.StatusCode = status;
                response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
            }
            return Task.FromResult(response);
        }
    }

    private sealed class CookieScope : IDisposable
    {
        private readonly string cookie = Config.COOKIE;
        private readonly bool international = Config.COOKIE_IS_INTL;
        private readonly string[] domains = Config.COOKIE_ALLOWED_DOMAINS;

        internal CookieScope()
        {
            Config.COOKIE = "SESSDATA=secret";
            Config.COOKIE_IS_INTL = false;
            Config.COOKIE_ALLOWED_DOMAINS = ["bilibili.com"];
        }

        public void Dispose()
        {
            Config.COOKIE = cookie;
            Config.COOKIE_IS_INTL = international;
            Config.COOKIE_ALLOWED_DOMAINS = domains;
        }
    }
}
