namespace BBDownT.Core;

public static class Config
{
    private static string cookie = "";
    private static string token = "";
    private static bool cookieIsIntl;
    private static readonly AsyncLocal<CredentialScope?> credentialScope = new();

    //For WEB
    public static string COOKIE
    {
        get => credentialScope.Value is { } scope ? scope.Cookie : cookie;
        set
        {
            if (credentialScope.Value is { } scope) scope.Cookie = value;
            else cookie = value;
        }
    }
    internal static bool COOKIE_IS_INTL
    {
        get => credentialScope.Value is { } scope ? scope.CookieIsIntl : cookieIsIntl;
        set
        {
            if (credentialScope.Value is { } scope) scope.CookieIsIntl = value;
            else cookieIsIntl = value;
        }
    }
    //For APP/TV
    public static string TOKEN
    {
        get => credentialScope.Value is { } scope ? scope.Token : token;
        set
        {
            if (credentialScope.Value is { } scope) scope.Token = value;
            else token = value;
        }
    }

    /// <summary>
    /// 在当前异步流程内固定使用指定的Cookie/Token，读写都只作用于本作用域，
    /// 不受其他并发任务改写全局值的影响，也不会改写全局值；用于服务器的解析预览等流程。
    /// 作用域内(包括被等待的子方法里)写入的新值，之后在同一作用域内都能读到。
    /// 解析相关的 HOST/EPHOST/TVHOST/AREA 在作用域内同样隔离，并固定从默认值开始：
    /// 下载任务会按自己的参数改写这几个全局值，解析预览不应沿用上一个任务的设置。
    /// <paramref name="international"/> 是作用域内的 COOKIE_IS_INTL(国际站Cookie)，同样不受并发任务影响。
    /// </summary>
    public static IDisposable UseCredentials(string cookie, string token, bool international = false)
    {
        var previous = credentialScope.Value;
        credentialScope.Value = new CredentialScope(cookie, token, international);
        return new ScopeRestorer(() => credentialScope.Value = previous);
    }

    /// <summary>
    /// 可变的持有对象：AsyncLocal 只保存引用，子方法里的写入对整个作用域可见
    /// </summary>
    private sealed class CredentialScope(string cookie, string token, bool international)
    {
        private volatile string cookieValue = cookie;
        private volatile bool cookieIsIntlValue = international;
        private volatile string tokenValue = token;
        private volatile string hostValue = DefaultHost;
        private volatile string epHostValue = DefaultHost;
        private volatile string tvHostValue = DefaultTvHost;
        private volatile string areaValue = "";
        public string Cookie { get => cookieValue; set => cookieValue = value; }
        public bool CookieIsIntl { get => cookieIsIntlValue; set => cookieIsIntlValue = value; }
        public string Token { get => tokenValue; set => tokenValue = value; }
        public string Host { get => hostValue; set => hostValue = value; }
        public string EpHost { get => epHostValue; set => epHostValue = value; }
        public string TvHost { get => tvHostValue; set => tvHostValue = value; }
        public string Area { get => areaValue; set => areaValue = value; }
    }

    private sealed class ScopeRestorer(Action restore) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) restore();
        }
    }
    //日志级别
    public static bool DEBUG_LOG { get; set; } = false;
    private const string DefaultHost = "api.bilibili.com";
    private const string DefaultTvHost = "api.snm0516.aisee.tv";
    private static string host = DefaultHost;
    private static string epHost = DefaultHost;
    private static string tvHost = DefaultTvHost;
    private static string area = "";
    //BiliPlus Host
    public static string HOST
    {
        get => credentialScope.Value is { } scope ? scope.Host : host;
        set
        {
            if (credentialScope.Value is { } scope) scope.Host = value;
            else host = value;
        }
    }
    //BiliPlus EP Host
    public static string EPHOST
    {
        get => credentialScope.Value is { } scope ? scope.EpHost : epHost;
        set
        {
            if (credentialScope.Value is { } scope) scope.EpHost = value;
            else epHost = value;
        }
    }
    //Bili Tv Api Host
    public static string TVHOST
    {
        get => credentialScope.Value is { } scope ? scope.TvHost : tvHost;
        set
        {
            if (credentialScope.Value is { } scope) scope.TvHost = value;
            else tvHost = value;
        }
    }
    //BiliPlus Area
    public static string AREA
    {
        get => credentialScope.Value is { } scope ? scope.Area : area;
        set
        {
            if (credentialScope.Value is { } scope) scope.Area = value;
            else area = value;
        }
    }

    public static string WBI { get; set; } = "";

    public static bool ALLOW_INSECURE_TLS { get; set; } = false;

    public static int MAX_GRPC_MESSAGE_BYTES { get; set; } = 64 * 1024 * 1024;

    public static string[] COOKIE_ALLOWED_DOMAINS { get; set; } =
    [
        "bilibili.com",
        "bilibili.tv",
        "biliintl.com",
        "bilivideo.com",
        "bilivideo.cn",
        "hdslb.com",
        "biliapi.net"
    ];

    public static readonly Dictionary<string, string> qualitys = new() {
        {"129","HDR Vivid" },
        {"127","8K 超高清" }, {"126","杜比视界" }, {"125","HDR 真彩" }, {"120","4K 超清" }, {"116","1080P 高帧率" },
        {"112","1080P 高码率" }, {"100","智能修复" }, {"80","1080P 高清" }, {"74","720P 高帧率" },
        {"64","720P 高清" }, {"48","720P 高清" }, {"32","480P 清晰" }, {"16","360P 流畅" },
        {"5","144P 流畅" }, {"6","240P 流畅" }
    };
}
