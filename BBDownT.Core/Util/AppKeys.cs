using System.Security.Cryptography;
using System.Text;

namespace BBDownT.Core.Util;

internal readonly record struct AppKey(string Key, string Secret)
{
    internal string Sign(string query)
        => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(query + Secret)));
}

internal static class AppKeys
{
    internal static readonly AppKey TvAndroid = new("4409e2ce8ffd12b8", "59b43e04ad6965f34319062b478f83dd");
    internal static readonly AppKey BstarAndroid = new("7d089525d3611b1c", "acd495b248ec528c2eed1e862d393126");

    internal static string UnixSeconds() => DateTimeOffset.Now.ToUnixTimeSeconds().ToString();
}
