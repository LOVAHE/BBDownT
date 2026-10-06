using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using System.Text.Json;
using BBDownT.Core.Entity;
using BBDownT.Core.Util;

namespace BBDownT;

// A page retry refreshes metadata and media addresses. Transport retries and
// completed-part validation remain owned by the HTTP client and downloader.
internal sealed class PageDownloadRetry
{
    private static readonly TimeSpan[] NetworkDelays =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)];
    private int networkRetries;
    private int otherRetries;

    internal bool TryGetDelay(Exception error, out TimeSpan delay)
    {
        delay = default;
        if (NetworkRetry.IsTransient(error))
        {
            if (networkRetries >= NetworkDelays.Length) return false;
            delay = NetworkDelays[networkRetries++];
            return true;
        }

        // Preserve the legacy short reparse budget for 403/404. These statuses
        // alone cannot distinguish an unusable media URL from denied access;
        // the transport layer must still fail without retrying that same URL.
        var reparseHttpFailure = false;
        for (Exception? cause = error; cause is not null; cause = cause.InnerException)
            if (cause is HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.NotFound })
                reparseHttpFailure = true;

        // Invalid options, malformed data, unsupported transports, explicit
        // cancellation and local storage failures cannot be cured by waiting.
        for (Exception? cause = error; cause is not null; cause = cause.InnerException)
        {
            if (cause is AudioLanguageUnavailableException or IntlApiException
                or ArgumentException or NotSupportedException or UnauthorizedAccessException
                or InvalidDataException or JsonException or AuthenticationException or OperationCanceledException
                or OutOfMemoryException or AccessViolationException)
                return false;
            if ((cause is IOException or HttpRequestException) && !reparseHttpFailure) return false;
        }
        if (otherRetries >= 2) return false;
        otherRetries++;
        delay = TimeSpan.FromSeconds(3);
        return true;
    }
}
