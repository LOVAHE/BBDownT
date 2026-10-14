using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using BBDownT.Core.Util;
using static BBDownT.Core.Logger;

namespace BBDownT;

internal static partial class AuthenticatedWebProfileStore
{
    internal const string FileName = "BBDownT.web.json";

    internal static void Configure(string directory)
    {
        if (!HTTPUtil.IsAutomaticUserAgent) return;

        var profile = LoadOrCreate(directory);
        HTTPUtil.ConfigureAuthenticatedBrowserProfile(
            profile,
            updated => Save(directory, updated));
    }

    internal static BrowserRequestProfile LoadOrCreate(string directory)
    {
        var path = Path.Combine(directory, FileName);
        if (File.Exists(path))
        {
            try
            {
                var profile = JsonSerializer.Deserialize(
                    File.ReadAllText(path),
                    BrowserRequestProfileJsonContext.Default.BrowserRequestProfile);
                if (profile is not null && profile.IsValid()) return profile;
                LogWarn($"{FileName}内容无效，已生成新的浏览器请求配置。");
            }
            catch (JsonException)
            {
                LogWarn($"{FileName}内容无效，已生成新的浏览器请求配置。");
            }
            catch (Exception ex)
            {
                LogWarn($"读取{FileName}失败，已生成新的浏览器请求配置。原因：{ErrorText.Describe(ex)}");
            }
        }

        var generated = BrowserRequestProfile.Create(Random.Shared);
        try
        {
            Save(directory, generated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogWarn($"保存{FileName}失败，将仅在本次运行中使用新的浏览器请求配置。原因：{ErrorText.Describe(ex)}");
        }
        return generated;
    }

    internal static void Save(string directory, BrowserRequestProfile profile)
    {
        if (!profile.IsValid()) throw new InvalidOperationException("拒绝保存无效的浏览器请求配置");
        Directory.CreateDirectory(directory);
        BBDownTLoginUtil.WritePrivateFile(
            Path.Combine(directory, FileName),
            JsonSerializer.SerializeToUtf8Bytes(
                profile,
                BrowserRequestProfileJsonContext.Default.BrowserRequestProfile));
    }

    [JsonSerializable(typeof(BrowserRequestProfile))]
    private partial class BrowserRequestProfileJsonContext : JsonSerializerContext { }
}
