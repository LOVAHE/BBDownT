using System.Net;
using BBDownT.Core.Util;

namespace BBDownT.Tests;

public class DeviceCookieHandlerTests
{
    [Fact]
    public async Task DeviceCookiesAreReplayedToBilibiliButAccountCookiesAreNot()
    {
        DeviceCookieHandler.Clear();
        var sent = new List<(string Host, string? Cookie)>();
        var transport = new StubTransport(request =>
        {
            sent.Add((request.RequestUri!.Host,
                request.Headers.TryGetValues("Cookie", out var values) ? string.Join("; ", values) : null));
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            if (sent.Count == 1)
            {
                response.Headers.Add("Set-Cookie", "buvid3=device-1; path=/; domain=.bilibili.com");
                response.Headers.Add("Set-Cookie", "SESSDATA=account; path=/; domain=.bilibili.com");
            }
            return response;
        });
        try
        {
            using var client = new HttpClient(new DeviceCookieHandler(transport));

            await client.GetAsync("https://www.bilibili.com/video/av1/");
            await client.GetAsync("https://api.bilibili.com/x/web-interface/view");
            using var withCookie = new HttpRequestMessage(HttpMethod.Get, "https://api.bilibili.com/x/player/wbi/playurl");
            withCookie.Headers.TryAddWithoutValidation("Cookie", "SESSDATA=user; buvid3=user-device");
            await client.SendAsync(withCookie);
            await client.GetAsync("https://cdn.example.test/media.m4s");

            Assert.Null(sent[0].Cookie);
            Assert.Equal("buvid3=device-1", sent[1].Cookie);
            Assert.Equal("SESSDATA=user; buvid3=user-device", sent[2].Cookie);
            Assert.Null(sent[3].Cookie);
        }
        finally
        {
            DeviceCookieHandler.Clear();
        }
    }

    private sealed class StubTransport(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
