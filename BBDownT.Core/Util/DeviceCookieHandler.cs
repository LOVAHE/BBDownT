using System.Collections.Concurrent;

namespace BBDownT.Core.Util;

internal sealed class DeviceCookieHandler(HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
{
    private static readonly string[] DeviceCookieNames = ["buvid3", "buvid4", "b_nut", "_uuid"];
    private static readonly ConcurrentDictionary<string, string> Cookies = new(StringComparer.Ordinal);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var bilibili = IsBilibili(request.RequestUri);
        if (bilibili) AddDeviceCookies(request);
        var response = await base.SendAsync(request, cancellationToken);
        if (bilibili && response.Headers.TryGetValues("Set-Cookie", out var values)) Remember(values);
        return response;
    }

    internal static void Clear() => Cookies.Clear();

    private static bool IsBilibili(Uri? uri)
        => uri is not null && uri.Scheme == Uri.UriSchemeHttps
            && (uri.Host.Equals("bilibili.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase));

    private static void Remember(IEnumerable<string> setCookieHeaders)
    {
        foreach (var header in setCookieHeaders)
        {
            var pair = header.Split(';', 2)[0].Split('=', 2);
            if (pair.Length == 2 && DeviceCookieNames.Contains(pair[0].Trim()) && pair[1].Trim().Length > 0)
                Cookies[pair[0].Trim()] = pair[1].Trim();
        }
    }

    private static void AddDeviceCookies(HttpRequestMessage request)
    {
        if (Cookies.IsEmpty) return;
        var existing = request.Headers.TryGetValues("Cookie", out var values) ? string.Join("; ", values) : "";
        var present = existing.Split(';').Select(pair => pair.Split('=', 2)[0].Trim()).ToHashSet(StringComparer.Ordinal);
        var missing = Cookies.Where(cookie => !present.Contains(cookie.Key)).Select(cookie => cookie.Key + "=" + cookie.Value).ToList();
        if (missing.Count == 0) return;
        request.Headers.Remove("Cookie");
        request.Headers.TryAddWithoutValidation("Cookie",
            string.Join("; ", existing.Length == 0 ? missing : missing.Prepend(existing.TrimEnd(';', ' '))));
    }
}
