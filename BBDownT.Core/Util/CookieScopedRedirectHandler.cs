using System.Net;

namespace BBDownT.Core.Util;

internal sealed class CookieScopedRedirectHandler(HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
{
    private const int MaxRedirects = 50;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        for (var redirects = 0; redirects < MaxRedirects && RedirectTarget(request.RequestUri!, response) is { } target; redirects++)
        {
            var status = response.StatusCode;
            response.Dispose();
            request.Headers.Authorization = null;
            if (request.Headers.Contains("Cookie") && !HTTPUtil.ShouldSendCookie(target.AbsoluteUri))
                request.Headers.Remove("Cookie");
            if (ForcesGet(status, request.Method))
            {
                request.Method = HttpMethod.Get;
                request.Content = null;
                if (request.Headers.TransferEncodingChunked == true) request.Headers.TransferEncodingChunked = false;
            }
            request.RequestUri = target;
            response = await base.SendAsync(request, cancellationToken);
        }
        return response;
    }

    private static Uri? RedirectTarget(Uri requestUri, HttpResponseMessage response)
    {
        if (response.StatusCode is not (HttpStatusCode.MultipleChoices or HttpStatusCode.MovedPermanently or HttpStatusCode.Found
                or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            || response.Headers.Location is not { } location)
            return null;
        var target = location.IsAbsoluteUri ? location : new Uri(requestUri, location);
        if (target.Scheme != Uri.UriSchemeHttps && (target.Scheme != Uri.UriSchemeHttp || requestUri.Scheme == Uri.UriSchemeHttps))
            return null;
        return requestUri.Fragment.Length > 0 && target.Fragment.Length == 0
            ? new UriBuilder(target) { Fragment = requestUri.Fragment }.Uri
            : target;
    }

    private static bool ForcesGet(HttpStatusCode status, HttpMethod method) => status switch
    {
        HttpStatusCode.MultipleChoices or HttpStatusCode.MovedPermanently or HttpStatusCode.Found => method == HttpMethod.Post,
        HttpStatusCode.SeeOther => method != HttpMethod.Get && method != HttpMethod.Head,
        _ => false
    };
}
