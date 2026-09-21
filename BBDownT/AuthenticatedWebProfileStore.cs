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

    internal static BrowserRequestProfile Configure(string directory)
    {
        if (!HTTPUtil.IsAutomaticUserAgent) return HTTPUtil.AuthenticatedBrowserProfile;

        var profile = LoadOrCreate(directory);
        HTTPUtil.ConfigureAuthenticatedBrowserProfile(
            profile,
            updated => Save(directory, updated));
        return profile;
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
            catch (Exception ex)
            {
                LogWarn($"读取{FileName}失败，已生成新的浏览器请求配置。原因：{ex.Message}");
            }
        }

        var generated = BrowserRequestProfile.Create(Random.Shared);
        Save(directory, generated);
        return generated;
    }

    internal static void Save(string directory, BrowserRequestProfile profile)
    {
        if (!profile.IsValid()) throw new InvalidOperationException("拒绝保存无效的浏览器请求配置");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName);
        var tempPath = path + $".writing-{Guid.NewGuid():N}";
        try
        {
            var json = JsonSerializer.Serialize(
                profile,
                BrowserRequestProfileJsonContext.Default.BrowserRequestProfile);
            File.WriteAllText(tempPath, json);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            File.Move(tempPath, path, true);
        }
        catch
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            throw;
        }
    }

    [JsonSerializable(typeof(BrowserRequestProfile))]
    private partial class BrowserRequestProfileJsonContext : JsonSerializerContext { }
}
