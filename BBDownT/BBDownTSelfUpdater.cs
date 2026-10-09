using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BBDownT;

internal static partial class BBDownTSelfUpdater
{
    internal const string ReleasesUrl = "https://api.github.com/repos/LOVAHE/BBDownT/releases?per_page=20";
    private const long MaxBinaryBytes = 256 * 1024 * 1024;

    internal sealed record ReleaseAsset(ReleaseVersion Version, string Name, Uri Url, long Size, string Sha256);

    internal sealed partial record ReleaseVersion(Version Core, int Stage, int Number) : IComparable<ReleaseVersion>
    {
        private const int Beta = 0, Candidate = 1, Stable = 2;

        public bool IsPrerelease => Stage != Stable;

        public static bool TryParse(string? text, [NotNullWhen(true)] out ReleaseVersion? version)
        {
            version = null;
            var match = Pattern().Match(text?.Split('+')[0].Trim() ?? "");
            if (!match.Success || !Version.TryParse(match.Groups["core"].Value, out var core)) return false;
            var stage = match.Groups["stage"].Value.ToLowerInvariant() switch { "beta" => Beta, "rc" => Candidate, _ => Stable };
            version = new(core, stage, match.Groups["number"].Length == 0 ? 0 : int.Parse(match.Groups["number"].Value));
            return true;
        }

        public int CompareTo(ReleaseVersion? other)
            => other is null ? 1 : Core != other.Core ? Core.CompareTo(other.Core)
                : Stage != other.Stage ? Stage.CompareTo(other.Stage) : Number.CompareTo(other.Number);

        public static bool operator >(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) > 0;
        public static bool operator <(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) < 0;

        public override string ToString() => Core + Stage switch { Beta => "-beta", Candidate => "-rc", _ => "" } + (IsPrerelease && Number > 0 ? Number.ToString() : "");

        [GeneratedRegex(@"^[vV]?(?<core>[0-9]+\.[0-9]+\.[0-9]+)(?:-(?<stage>beta|rc)(?<number>[0-9]{0,6}))?\z", RegexOptions.IgnoreCase)]
        private static partial Regex Pattern();
    }
    internal enum InstallationKind { Standalone, DotnetTool, Managed }

    [UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "An empty assembly location identifies the standalone builds supported by self-update.")]
    internal static async Task<int> RunAsync()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += onCancel;
        try
        {
            string location = Assembly.GetExecutingAssembly().Location;
            string? entryPath = string.IsNullOrEmpty(location) ? Environment.ProcessPath : location;
            bool toolSettings = !string.IsNullOrEmpty(entryPath)
                && File.Exists(Path.Combine(Path.GetDirectoryName(entryPath)!, "DotnetToolSettings.xml"));
            var installation = DetectInstallation(Environment.ProcessPath, location, toolSettings);
            if (installation == InstallationKind.DotnetTool)
                throw new InvalidOperationException("检测到 Dotnet Tool 安装，请按安装方式更新：全局使用 dotnet tool update --global BBDownT；本地使用 dotnet tool update --local BBDownT；自定义目录使用 dotnet tool update --tool-path <目录> BBDownT。");
            if (installation != InstallationKind.Standalone)
                throw new InvalidOperationException("当前为 DLL 或开发运行方式，请更新原发布目录或改用官方独立可执行程序；不会替换 dotnet 或启动器。");

            string platform = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "osx" : "";
            string assetName = GetAssetName(platform, RuntimeInformation.ProcessArchitecture);
            string executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定当前程序路径");
            var file = new FileInfo(executable);
            if (file.LinkTarget is not null)
                executable = file.ResolveLinkTarget(true)?.FullName ?? throw new IOException("无法解析程序符号链接");

            var informational = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!ReleaseVersion.TryParse(informational, out var current))
                throw new InvalidOperationException("无法识别当前程序版本");
            // Update traffic never shares Bilibili cookies or permissive TLS settings.
            using var client = new HttpClient(new HttpClientHandler { UseCookies = false })
            {
                Timeout = Timeout.InfiniteTimeSpan,
                MaxResponseContentBufferSize = 2 * 1024 * 1024
            };
            Console.WriteLine($"检查最新版本（当前 {current}）...");
            var result = await UpdateAsync(client, executable, current, assetName,
                OperatingSystem.IsWindows(), VerifyExecutableAsync, cancellation.Token);
            if (result is null)
                Console.WriteLine("当前已是最新版本，无需更新。");
            else
            {
                Console.WriteLine($"已更新至 {result.Value.Version}，下次启动生效：{executable}");
                Console.WriteLine($"旧版本备份：{result.Value.BackupPath}");
            }
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("更新已取消或超时，当前程序未替换。");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"更新失败：{ex.Message}");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    internal static InstallationKind DetectInstallation(string? executable, string assemblyLocation, bool hasToolSettings)
    {
        // A packaged .NET tool ships this marker alongside its entry assembly.
        // Even without the marker, a managed entry assembly must never cause us
        // to overwrite dotnet or the tool shim. Do not guess global/local scope.
        if (hasToolSettings) return InstallationKind.DotnetTool;
        if (!string.IsNullOrEmpty(assemblyLocation)) return InstallationKind.Managed;
        if (string.IsNullOrEmpty(executable)
            || Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return InstallationKind.Managed;
        return InstallationKind.Standalone;
    }

    internal static string GetAssetName(string platform, Architecture architecture)
    {
        if (platform is not ("win" or "linux" or "osx") || architecture is not (Architecture.X64 or Architecture.Arm64))
            throw new PlatformNotSupportedException("当前系统或架构没有可用的官方更新包");
        string arch = architecture == Architecture.X64 ? "x64" : "arm64";
        return $"BBDownT_{platform}-{arch}" + (platform == "win" ? ".exe" : "");
    }

    internal static ReleaseAsset? SelectAsset(string json, ReleaseVersion current, string assetName)
    {
        using var document = JsonDocument.Parse(json);
        JsonElement? newest = null;
        ReleaseVersion? latest = null;
        foreach (var candidate in document.RootElement.EnumerateArray())
        {
            if (candidate.GetProperty("draft").GetBoolean()
                || !ReleaseVersion.TryParse(candidate.GetProperty("tag_name").GetString(), out var version)) continue;
            var prerelease = version.IsPrerelease || candidate.GetProperty("prerelease").GetBoolean();
            if ((prerelease && !(current.IsPrerelease && version.Core == current.Core)) || !(version > current) || (latest is not null && !(version > latest))) continue;
            (newest, latest) = (candidate, version);
        }
        if (newest is not { } release || latest is null) return null;
        string tag = release.GetProperty("tag_name").GetString()!;

        var matches = release.GetProperty("assets").EnumerateArray()
            .Where(asset => asset.GetProperty("name").GetString() == assetName).ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException($"正式版本中没有唯一匹配的更新包：{assetName}");
        var asset = matches[0];
        long size = asset.GetProperty("size").GetInt64();
        string digest = asset.GetProperty("digest").GetString() ?? "";
        string expectedUrl = $"https://github.com/LOVAHE/BBDownT/releases/download/{Uri.EscapeDataString(tag)}/{assetName}";
        if (asset.GetProperty("state").GetString() != "uploaded" || size <= 0 || size > MaxBinaryBytes
            || !digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71
            || !digest[7..].All(Uri.IsHexDigit)
            || asset.GetProperty("browser_download_url").GetString() != expectedUrl)
            throw new InvalidDataException("更新包的地址、大小或 SHA-256 校验信息无效");
        return new ReleaseAsset(latest, assetName, new Uri(expectedUrl), size, digest[7..]);
    }

    // The injected client and version probe allow offline verification without
    // replacing the test host or contacting GitHub. A backup is retained on success.
    internal static async Task<(ReleaseVersion Version, string BackupPath)?> UpdateAsync(
        HttpClient client, string executable, ReleaseVersion current, string assetName, bool windows,
        Func<string, ReleaseVersion, CancellationToken, Task> verifyExecutable, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(ReleasesUrl);
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var asset = SelectAsset(await response.Content.ReadAsStringAsync(cancellationToken), current, assetName);
        if (asset is null) return null;

        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable)) throw new FileNotFoundException("当前可执行文件不存在", executable);
        string lockPath = executable + ".update.lock";
        using var updateLock = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        string suffix = Guid.NewGuid().ToString("N");
        string staged = executable + ".update-" + suffix + (windows ? ".exe" : ".tmp");
        string backup = executable + ".backup-" + current + "-" + suffix;
        bool stagedCreated = false;
        try
        {
            Console.WriteLine($"下载 {asset.Name}（{asset.Version}）...");
            using var downloadRequest = CreateRequest(asset.Url.AbsoluteUri);
            using var download = await client.SendAsync(downloadRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            download.EnsureSuccessStatusCode();
            if (download.StatusCode != HttpStatusCode.OK)
                throw new InvalidDataException("更新包下载未返回完整文件");
            if (download.Content.Headers.ContentLength is long contentLength && contentLength != asset.Size)
                throw new InvalidDataException("更新包大小与发布信息不一致");
            await using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stagedCreated = true;
                await using var input = await download.Content.ReadAsStreamAsync(cancellationToken);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[81920];
                long received = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    received += count;
                    if (received > asset.Size) throw new InvalidDataException("更新包超过预期大小");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                }
                if (received != asset.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("更新包 SHA-256 或大小校验失败，未替换当前程序");
                output.Flush(flushToDisk: true);
            }

            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(staged, File.GetUnixFileMode(executable));
            await verifyExecutable(staged, asset.Version, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Install(staged, executable, backup, windows);
            DeleteOlderBackups(executable, backup);
            return (asset.Version, backup);
        }
        finally
        {
            if (stagedCreated && File.Exists(staged)) File.Delete(staged);
        }
    }

    internal static void Install(string staged, string executable, string backup, bool windows)
    {
        // Unix supports atomic replacement of a running executable. Windows cannot
        // unlink a loaded image, but can rename it when existing handles allow it;
        // keep the old image as a backup and roll back if the second move fails.
        if (windows)
        {
            File.Move(executable, backup);
            try { File.Move(staged, executable); }
            catch (Exception installError)
            {
                try { File.Move(backup, executable); }
                catch (Exception rollbackError)
                {
                    throw new IOException($"替换及回滚失败，旧程序仍保存在 {backup}，请手动恢复原文件名。",
                        new AggregateException(installError, rollbackError));
                }
                throw;
            }
        }
        else
        {
            // Create-new protects unrelated files and identifies ownership even if
            // copying the backup fails partway through (e.g. a full disk).
            bool backupCreated = false;
            try
            {
                using var source = File.OpenRead(executable);
                using var destination = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                backupCreated = true;
                source.CopyTo(destination);
                destination.Flush(flushToDisk: true);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(backup, File.GetUnixFileMode(executable));
            }
            catch
            {
                if (backupCreated) File.Delete(backup);
                throw;
            }
            try { File.Move(staged, executable, overwrite: true); }
            catch (Exception ex)
            {
                throw new IOException($"替换失败，旧版本备份保存在 {backup}。", ex);
            }
        }
    }

    internal static void DeleteOlderBackups(string executable, string keep)
    {
        string name = Path.GetFileName(executable);
        string[] candidates;
        try
        {
            candidates = Directory.GetFiles(Path.GetDirectoryName(executable)!, name + ".backup-*");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine("无法检查更早的版本备份，请手动清理。");
            return;
        }
        foreach (var path in candidates)
        {
            string file = Path.GetFileName(path);
            if (file == Path.GetFileName(keep) || !file.StartsWith(name, StringComparison.Ordinal)
                || !BackupSuffix().IsMatch(file[name.Length..])) continue;
            try
            {
                File.Delete(path);
                Console.WriteLine($"已删除更早的版本备份：{path}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"无法删除更早的版本备份，请手动删除：{path}");
            }
        }
    }

    [GeneratedRegex(@"^\.backup-[0-9]+\.[0-9]+\.[0-9]+(?:-(?:beta|rc)[0-9]*)?-[0-9a-f]{32}\z")]
    private static partial Regex BackupSuffix();

    private static HttpRequestMessage CreateRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", "BBDownT-SelfUpdater");
        return request;
    }

    private static async Task VerifyExecutableAsync(string staged, ReleaseVersion expected, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(staged)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("--version");
        using var process = Process.Start(start) ?? throw new IOException("无法启动更新包进行版本检查");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            string text = (await output).Trim().Split('+')[0];
            await error;
            if (process.ExitCode != 0 || !ReleaseVersion.TryParse(text, out var version) || version != expected)
                throw new InvalidDataException("更新包无法运行或版本号与发布信息不一致");
        }
        catch
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            throw;
        }
    }
}
