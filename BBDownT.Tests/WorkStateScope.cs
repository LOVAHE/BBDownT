using BBDownT.Core;
using BBDownT.Core.Util;

namespace BBDownT.Tests;

internal sealed class WorkStateScope : IDisposable
{
    private readonly string userAgent = HTTPUtil.IsAutomaticUserAgent ? "" : HTTPUtil.UserAgent;
    private readonly bool debug = Config.DEBUG_LOG;
    private readonly bool serverDebug = Program.ServerDebug;
    private readonly string host = Config.HOST, epHost = Config.EPHOST, tvHost = Config.TVHOST, area = Config.AREA;
    private readonly string cookie = Config.COOKIE, token = Config.TOKEN;
    private readonly bool cookieIsIntl = Config.COOKIE_IS_INTL;
    private readonly string[] cookieAllowedDomains = Config.COOKIE_ALLOWED_DOMAINS;

    public void Dispose()
    {
        HTTPUtil.UserAgent = userAgent;
        (Config.DEBUG_LOG, Program.ServerDebug) = (debug, serverDebug);
        (Config.HOST, Config.EPHOST, Config.TVHOST, Config.AREA) = (host, epHost, tvHost, area);
        (Config.COOKIE, Config.TOKEN, Config.COOKIE_IS_INTL) = (cookie, token, cookieIsIntl);
        Config.COOKIE_ALLOWED_DOMAINS = cookieAllowedDomains;
    }
}
