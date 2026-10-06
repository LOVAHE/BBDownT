using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text;
using BBDownT.Core;
using BBDownT.Core.Util;

namespace BBDownT.Tests;

public class WebRequestRetryTests
{
    [Fact]
    public async Task Get_UsesNewRequestsAndTheSameBrowserIdentityAcrossNetworkRetries()
    {
        var profile = HTTPUtil.AuthenticatedBrowserProfile;
        var requests = new List<HttpRequestMessage>();
        var headers = new List<string>();
        var waits = new List<TimeSpan>();
        using var handler = new MemoryHandler((request, _) =>
        {
            requests.Add(request);
            headers.Add(request.Headers.ToString());
            if (requests.Count == 1) throw new HttpRequestException(HttpRequestError.NameResolutionError);
            return Task.FromResult(Response(request, requests.Count == 2
                ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK, new StringContent("complete")));
        });
        using var client = new HttpClient(handler);

        var result = await HTTPUtil.GetWebSourceAsync(client, "https://api.bilibili.com/example",
            sendCookie: false, forceAuthenticatedProfile: true,
            delay: (wait, _) => { waits.Add(wait); return Task.CompletedTask; }, log: _ => { });

        Assert.Equal("complete", result);
        Assert.Equal(3, requests.Count);
        Assert.NotSame(requests[0], requests[1]);
        Assert.NotSame(requests[1], requests[2]);
        Assert.All(headers, header => Assert.Equal(headers[0], header));
        Assert.Contains(profile.UserAgent, headers[0]);
        Assert.Equal(profile, HTTPUtil.AuthenticatedBrowserProfile);
        Assert.Equal(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3) }, waits);
    }

    [Fact]
    public async Task Get_RetriesBodyInterruptionAndDisposesEveryResponse()
    {
        var partial = new MemoryContent((stream, _) =>
        {
            stream.Write(Encoding.UTF8.GetBytes("partial"));
            throw new HttpIOException(HttpRequestError.ResponseEnded, "connection ended");
        });
        var complete = new MemoryContent((stream, token) => stream.WriteAsync(Encoding.UTF8.GetBytes("complete"), token).AsTask());
        var attempts = 0;
        var waits = new List<TimeSpan>();
        using var handler = new MemoryHandler((request, _) => Task.FromResult(Response(request,
            HttpStatusCode.OK, ++attempts == 1 ? partial : complete)));
        using var client = new HttpClient(handler);

        var result = await HTTPUtil.GetWebSourceAsync(client, "https://example.test/body", "test-client/1",
            delay: (wait, _) => { waits.Add(wait); return Task.CompletedTask; }, log: _ => { });

        Assert.Equal("complete", result);
        Assert.Equal(2, attempts);
        Assert.True(partial.Disposed);
        Assert.True(complete.Disposed);
        Assert.Equal(TimeSpan.FromSeconds(1), Assert.Single(waits));
    }

    [Fact]
    public async Task Get_RetriesPlainBodyIoInterruptionAndRecovers()
    {
        var failure = new IOException("remote read interrupted");
        var partial = new MemoryContent((stream, _) =>
        {
            stream.Write(Encoding.UTF8.GetBytes("partial"));
            throw failure;
        });
        var attempts = 0;
        var logs = new List<string>();
        using var handler = new MemoryHandler((request, _) => Task.FromResult(Response(request,
            HttpStatusCode.OK, ++attempts == 1 ? partial : new StringContent("complete"))));
        using var client = new HttpClient(handler);
        Assert.Equal("complete", await HTTPUtil.GetWebSourceAsync(client, "https://example.test/body", "test-client/1",
            delay: (_, _) => Task.CompletedTask, log: logs.Add));
        Assert.Equal(2, attempts);
        Assert.True(partial.Disposed);
        Assert.Contains(nameof(DownloadInterruptedException), Assert.Single(logs));
    }

    [Fact]
    public async Task Get_PlainBodyIoExhaustionPreservesFrameworkWrapperAndOriginalCause()
    {
        var failure = new IOException("remote read interrupted");
        var contents = new List<MemoryContent>();
        using var handler = new MemoryHandler((request, _) =>
        {
            var content = new MemoryContent((_, _) => throw failure);
            contents.Add(content);
            return Task.FromResult(Response(request, HttpStatusCode.OK, content));
        });
        using var client = new HttpClient(handler);
        var actual = await Assert.ThrowsAsync<DownloadInterruptedException>(() => HTTPUtil.GetWebSourceAsync(client,
            "https://example.test/body", "test-client/1", delay: (_, _) => Task.CompletedTask, log: _ => { }));
        var frameworkWrapper = Assert.IsType<HttpRequestException>(actual.InnerException);
        Assert.Equal(HttpRequestError.Unknown, frameworkWrapper.HttpRequestError);
        Assert.Same(failure, frameworkWrapper.InnerException);
        Assert.Equal(4, contents.Count);
        Assert.All(contents, content => Assert.True(content.Disposed));
    }

    [Theory]
    [InlineData("protocol")]
    [InlineData("invalid-data")]
    [InlineData("wrapped-invalid-data")]
    public async Task Get_DeterministicallyBadBodyDoesNotRetry(string kind)
    {
        Exception failure = kind switch
        {
            "protocol" => new HttpIOException(HttpRequestError.InvalidResponse, "invalid HTTP framing"),
            "invalid-data" => new InvalidDataException("invalid compressed response"),
            _ => new IOException("response decoding failed", new InvalidDataException("invalid compressed response"))
        };
        var attempts = 0;
        var content = new MemoryContent((_, _) => throw failure);
        using var handler = new MemoryHandler((request, _) =>
        {
            attempts++;
            return Task.FromResult(Response(request, HttpStatusCode.OK, content));
        });
        using var client = new HttpClient(handler);
        Task<string> ReadBody() => HTTPUtil.GetWebSourceAsync(client,
            "https://example.test/body", "test-client/1",
            delay: (_, _) => throw new InvalidOperationException("must not retry"), log: _ => { });
        if (kind == "invalid-data")
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidDataException>(ReadBody));
        else
            Assert.Same(failure, (await Assert.ThrowsAsync<HttpRequestException>(ReadBody)).InnerException);
        Assert.Equal(1, attempts);
        Assert.True(content.Disposed);
    }

    [Fact]
    public async Task Get_CorruptGzipBodyDoesNotRetry()
    {
        var attempts = 0;
        var content = new MemoryContent(async (stream, token) =>
        {
            using var compressed = new MemoryStream(new byte[32]);
            using var gzip = new System.IO.Compression.GZipStream(compressed, System.IO.Compression.CompressionMode.Decompress);
            await gzip.CopyToAsync(stream, token);
        });
        using var handler = new MemoryHandler((request, _) =>
        {
            attempts++;
            return Task.FromResult(Response(request, HttpStatusCode.OK, content));
        });
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => HTTPUtil.GetWebSourceAsync(client,
            "https://example.test/body", "test-client/1",
            delay: (_, _) => throw new InvalidOperationException("must not retry"), log: _ => { }));
        Assert.Equal(1, attempts);
        Assert.True(content.Disposed);
    }

    [Fact]
    public async Task Get_RetryAfterDeltaAndDateAreCapped()
    {
        var attempts = 0;
        var waits = new List<TimeSpan>();
        using var handler = new MemoryHandler((request, _) =>
        {
            var response = Response(request, ++attempts < 3 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK,
                new StringContent("complete"));
            if (attempts == 1) response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1));
            if (attempts == 2) response.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddHours(1));
            return Task.FromResult(response);
        });
        using var client = new HttpClient(handler);

        Assert.Equal("complete", await HTTPUtil.GetWebSourceAsync(client, "https://example.test/status", "test-client/1",
            delay: (wait, _) => { waits.Add(wait); return Task.CompletedTask; }, log: _ => { }));
        Assert.Equal(new[] { TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60) }, waits);
    }

    [Fact]
    public async Task Get_412RotatesOnceAndNetworkRetriesKeepTheRotatedIdentity()
    {
        var original = HTTPUtil.AuthenticatedBrowserProfile;
        var rotated = BrowserRequestProfile.CreateFirefox(new Random(42));
        var identities = new List<string>();
        var rotations = 0;
        var waits = new List<TimeSpan>();
        using var handler = new MemoryHandler((request, _) =>
        {
            identities.Add(string.Join(' ', request.Headers.GetValues("User-Agent")));
            return Task.FromResult(Response(request, identities.Count == 2
                ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.PreconditionFailed, new StringContent("")));
        });
        using var client = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => HTTPUtil.GetWebSourceAsync(client,
            "https://api.bilibili.com/example", sendCookie: false, forceAuthenticatedProfile: true,
            delay: (wait, _) => { waits.Add(wait); return Task.CompletedTask; }, log: _ => { },
            rotateIdentity: failed =>
            {
                rotations++;
                Assert.Equal(original, failed.BrowserProfile);
                return new HTTPUtil.RequestIdentity(rotated.UserAgent, rotated);
            }));

        Assert.Equal(HttpStatusCode.PreconditionFailed, error.StatusCode);
        Assert.Equal(1, rotations);
        Assert.Equal(new[] { original.UserAgent, rotated.UserAgent, rotated.UserAgent }, identities);
        Assert.Equal(TimeSpan.FromSeconds(1), Assert.Single(waits));
        Assert.Equal(original, HTTPUtil.AuthenticatedBrowserProfile);
    }

    [Fact]
    public async Task ExplicitUserAgent_412DoesNotRotateOrRetry()
    {
        var attempts = 0;
        using var handler = new MemoryHandler((request, _) =>
        {
            attempts++;
            return Task.FromResult(Response(request, HttpStatusCode.PreconditionFailed, new StringContent("")));
        });
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => HTTPUtil.GetWebSourceAsync(client,
            "https://example.test/status", "test-client/1",
            delay: (_, _) => throw new InvalidOperationException("must not retry"), log: _ => { },
            rotateIdentity: _ => throw new InvalidOperationException("must not rotate")));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Head_RetriesTransientStatusesWithFreshRequests()
    {
        var requests = new List<HttpRequestMessage>();
        using var handler = new MemoryHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Head, request.Method);
            requests.Add(request);
            return Task.FromResult(Response(request, requests.Count == 1
                ? HttpStatusCode.RequestTimeout : HttpStatusCode.OK, new StringContent("")));
        });
        using var client = new HttpClient(handler);
        Assert.Equal("https://example.test/location", await HTTPUtil.GetWebLocationAsync(client,
            "https://example.test/location", sendCookie: false, delay: (_, _) => Task.CompletedTask, log: _ => { }));
        Assert.Equal(2, requests.Count);
        Assert.NotSame(requests[0], requests[1]);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    public async Task Get_PermanentStatusDoesNotRetryAndDisposesResponse(int status)
    {
        var content = new MemoryContent((_, _) => Task.CompletedTask);
        var attempts = 0;
        using var handler = new MemoryHandler((request, _) =>
        {
            attempts++;
            return Task.FromResult(Response(request, (HttpStatusCode)status, content));
        });
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => HTTPUtil.GetWebSourceAsync(client,
            "https://example.test/status", "test-client/1",
            delay: (_, _) => throw new InvalidOperationException("must not retry"), log: _ => { }));
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Equal(1, attempts);
        Assert.True(content.Disposed);
    }

    [Fact]
    public async Task Get_CertificateFailureDoesNotRetryOrWrapException()
    {
        var failure = new HttpRequestException(HttpRequestError.SecureConnectionError, "private URL",
            new AuthenticationException("certificate failure"));
        using var handler = new MemoryHandler((_, _) => throw failure);
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => HTTPUtil.GetWebSourceAsync(client,
            "https://example.test/status", "test-client/1",
            delay: (_, _) => throw new InvalidOperationException("must not retry"), log: _ => { }));
        Assert.Same(failure, error);
    }

    [Fact]
    public async Task Get_ExhaustsItsBudgetAndDisposesAllFailedResponses()
    {
        var contents = new List<MemoryContent>();
        var waits = new List<TimeSpan>();
        using var handler = new MemoryHandler((request, _) =>
        {
            var content = new MemoryContent((_, _) => Task.CompletedTask);
            contents.Add(content);
            return Task.FromResult(Response(request, HttpStatusCode.BadGateway, content));
        });
        using var client = new HttpClient(handler);
        var failure = await Assert.ThrowsAsync<RetryableHttpStatusException>(() => HTTPUtil.GetWebSourceAsync(client,
            "https://example.test/status", "test-client/1",
            delay: (wait, _) => { waits.Add(wait); return Task.CompletedTask; }, log: _ => { }));
        Assert.Equal(HttpStatusCode.BadGateway, failure.StatusCode);
        Assert.Equal(4, contents.Count);
        Assert.All(contents, content => Assert.True(content.Disposed));
        Assert.Equal(NetworkRetry.RequestDelays, waits);
    }

    [Fact]
    public async Task Get_ExplicitHttpTimeoutRetriesButOrdinaryCancellationDoesNot()
    {
        var attempts = 0;
        using var handler = new MemoryHandler((request, _) =>
        {
            if (++attempts == 1) throw new TaskCanceledException("timeout", new TimeoutException("HTTP timeout"));
            return Task.FromResult(Response(request, HttpStatusCode.OK, new StringContent("complete")));
        });
        using var client = new HttpClient(handler);
        Assert.Equal("complete", await HTTPUtil.GetWebSourceAsync(client, "https://example.test/body", "test-client/1",
            delay: (_, _) => Task.CompletedTask, log: _ => { }));
        Assert.Equal(2, attempts);

        var failure = new OperationCanceledException("explicit cancellation");
        using var cancelledHandler = new MemoryHandler((_, _) => throw failure);
        using var cancelledClient = new HttpClient(cancelledHandler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HTTPUtil.GetWebSourceAsync(cancelledClient,
            "https://example.test/body", "test-client/1",
            delay: (_, _) => throw new InvalidOperationException("must not retry"), log: _ => { }));
    }

    [Fact]
    public async Task Head_CallerCancellationStopsBackoff()
    {
        using var caller = new CancellationTokenSource();
        var attempts = 0;
        using var handler = new MemoryHandler((request, _) =>
        {
            attempts++;
            return Task.FromResult(Response(request, HttpStatusCode.ServiceUnavailable, new StringContent("")));
        });
        using var client = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HTTPUtil.GetWebLocationAsync(client,
            "https://example.test/location", sendCookie: false, cancellationToken: caller.Token,
            delay: (_, token) =>
            {
                Assert.Equal(caller.Token, token);
                caller.Cancel();
                return Task.Delay(Timeout.InfiniteTimeSpan, token);
            }, log: _ => { }));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task IntlCookiePolicy_RemainsRestrictedAcrossRetriesAndRedirectIsNotFollowed()
    {
        var previousCookie = Config.COOKIE;
        var previousIntl = Config.COOKIE_IS_INTL;
        try
        {
            Config.COOKIE = "SESSDATA=test-only";
            Config.COOKIE_IS_INTL = true;
            var allowedCookies = new List<string?>();
            using var handler = new MemoryHandler((request, _) =>
            {
                allowedCookies.Add(request.Headers.TryGetValues("Cookie", out var cookies) ? Assert.Single(cookies) : null);
                var response = Response(request, allowedCookies.Count == 1
                    ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Found, new StringContent(""));
                response.Headers.Location = new Uri("https://example.test/redirect");
                return Task.FromResult(response);
            });
            using var client = new HttpClient(handler);
            var failure = await Assert.ThrowsAsync<HttpRequestException>(() => HTTPUtil.GetWebSourceAsync(client,
                "https://www.bilibili.tv/example", "test-client/1",
                delay: (_, _) => Task.CompletedTask, log: _ => { }));
            Assert.Equal(HttpStatusCode.Found, failure.StatusCode);
            Assert.Equal(new[] { "SESSDATA=test-only", "SESSDATA=test-only" }, allowedCookies);
            Assert.False(HTTPUtil.ShouldSendCookie("https://example.test/redirect"));
            using var internationalHandler = HTTPUtil.CreateWebHandler(useCookies: false, allowRedirects: false);
            Assert.False(internationalHandler.UseCookies);
            Assert.False(internationalHandler.AllowAutoRedirect);
            Assert.Same(HTTPUtil.IntlApiHttpClient, HTTPUtil.GetWebHttpClient(international: true));

            var deniedCookies = new List<bool>();
            using var deniedHandler = new MemoryHandler((request, _) =>
            {
                deniedCookies.Add(request.Headers.Contains("Cookie"));
                return Task.FromResult(Response(request, deniedCookies.Count == 1
                    ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK, new StringContent("complete")));
            });
            using var deniedClient = new HttpClient(deniedHandler);
            Assert.Equal("complete", await HTTPUtil.GetWebSourceAsync(deniedClient,
                "https://example.test/denied", "test-client/1", delay: (_, _) => Task.CompletedTask, log: _ => { }));
            Assert.Equal(new[] { false, false }, deniedCookies);
        }
        finally
        {
            Config.COOKIE = previousCookie;
            Config.COOKIE_IS_INTL = previousIntl;
        }
    }

    private static HttpResponseMessage Response(HttpRequestMessage request, HttpStatusCode status, HttpContent content)
        => new(status) { RequestMessage = request, Content = content };

    private sealed class MemoryHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }

    private sealed class MemoryContent(Func<Stream, CancellationToken, Task> write) : HttpContent
    {
        internal bool Disposed { get; private set; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => write(stream, CancellationToken.None);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
            => write(stream, cancellationToken);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
