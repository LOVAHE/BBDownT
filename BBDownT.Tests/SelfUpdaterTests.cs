using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BBDownT.Tests;

public class SelfUpdaterTests
{
    private const string AssetName = "BBDownT_linux-x64";
    private static readonly BBDownTSelfUpdater.ReleaseVersion CurrentVersion = Parse("1.0.0");
    private static readonly BBDownTSelfUpdater.ReleaseVersion LatestVersion = Parse("2.0.0");
    private static readonly byte[] OriginalBytes = Encoding.UTF8.GetBytes("original executable");
    private static readonly byte[] UpdatedBytes = Encoding.UTF8.GetBytes("updated executable");

    [Fact]
    public void SelectAsset_SelectsTheUniqueAssetFromANewerStableRelease()
    {
        var json = CreateReleaseJson();

        var asset = Assert.IsType<BBDownTSelfUpdater.ReleaseAsset>(
            BBDownTSelfUpdater.SelectAsset(json, CurrentVersion, AssetName));

        Assert.Equal(LatestVersion, asset.Version);
        Assert.Equal(AssetName, asset.Name);
        Assert.Equal(ExpectedAssetUrl, asset.Url.AbsoluteUri);
        Assert.Equal(UpdatedBytes.Length, asset.Size);
        Assert.Equal(Sha256(UpdatedBytes), asset.Sha256);
    }

    [Theory]
    [InlineData("v1.0.0")]
    [InlineData("v0.9.0")]
    [InlineData("v2.0.0-rc1")]
    [InlineData("latest")]
    public void SelectAsset_ReturnsNullWhenNoEligibleReleaseIsNewer(string tag)
    {
        var json = CreateReleaseJson(tag: tag);

        Assert.Null(BBDownTSelfUpdater.SelectAsset(json, CurrentVersion, AssetName));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void SelectAsset_SkipsDraftsAndLeavesPrereleasesToPrereleaseUsers(bool draft, bool prerelease)
    {
        Assert.Null(BBDownTSelfUpdater.SelectAsset(CreateReleaseJson(draft: draft, prerelease: prerelease), CurrentVersion, AssetName));
    }

    [Fact]
    public void ReleaseVersions_OrderBetasBeforeReleaseCandidatesBeforeTheRelease()
    {
        string[] ordered = ["2.1.7", "2.2.0-beta", "2.2.0-beta1", "2.2.0-beta2", "2.2.0-beta10", "2.2.0-rc", "2.2.0-rc1", "2.2.0-RC2", "v2.2.0", "2.2.1-beta1"];

        for (var i = 1; i < ordered.Length; i++) Assert.True(Parse(ordered[i]) > Parse(ordered[i - 1]), ordered[i]);
        Assert.Equal("2.2.0-rc2", Parse("2.2.0-RC2+abc").ToString());
        Assert.Equal("2.2.0-rc", Parse("v2.2.0-rc").ToString());
        Assert.False(BBDownTSelfUpdater.ReleaseVersion.TryParse("2.2.0-alpha1", out _));
        Assert.False(BBDownTSelfUpdater.ReleaseVersion.TryParse("2.2-beta1", out _));
    }

    [Theory]
    [InlineData("2.2.0-beta1", "v2.2.0-beta2,v2.2.0-rc1", "v2.2.0-rc1")]
    [InlineData("2.2.0-rc3", "v2.2.0-rc2,v2.2.0", "v2.2.0")]
    [InlineData("2.1.7", "v2.2.0,v2.3.0-beta1", "v2.2.0")]
    [InlineData("2.2.0-rc1", "v2.2.0,v2.3.0-beta1", "v2.2.0")]
    [InlineData("2.2.0-beta1", "v2.2.0-rc1,v2.3.0-beta1", "v2.2.0-rc1")]
    public void SelectAsset_PicksTheNewestEligibleRelease(string current, string tags, string expectedTag)
    {
        var json = "[" + string.Join(",", tags.Split(',').Select(tag => CreateRelease(tag, prerelease: tag.Contains('-')))) + "]";

        var asset = Assert.IsType<BBDownTSelfUpdater.ReleaseAsset>(BBDownTSelfUpdater.SelectAsset(json, Parse(current), AssetName));

        Assert.Equal(Parse(expectedTag), asset.Version);
        Assert.EndsWith($"/{expectedTag}/{AssetName}", asset.Url.AbsoluteUri);
    }

    [Theory]
    [InlineData("asset-missing")]
    [InlineData("digest-invalid")]
    [InlineData("foreign-url")]
    [InlineData("duplicate-asset")]
    public void SelectAsset_RejectsUntrustedOrAmbiguousReleaseMetadata(string scenario)
    {
        var json = scenario switch
        {
            "asset-missing" => CreateReleaseJson(assetCount: 0),
            "digest-invalid" => CreateReleaseJson(digest: "sha256:not-a-digest"),
            "foreign-url" => CreateReleaseJson(downloadUrl: "https://example.com/BBDownT_linux-x64"),
            "duplicate-asset" => CreateReleaseJson(assetCount: 2),
            _ => throw new InvalidOperationException()
        };

        Assert.Throws<InvalidDataException>(
            () => BBDownTSelfUpdater.SelectAsset(json, CurrentVersion, AssetName));
    }

    [Theory]
    [InlineData("win", Architecture.X64, "BBDownT_win-x64.exe")]
    [InlineData("win", Architecture.Arm64, "BBDownT_win-arm64.exe")]
    [InlineData("linux", Architecture.X64, "BBDownT_linux-x64")]
    [InlineData("linux", Architecture.Arm64, "BBDownT_linux-arm64")]
    [InlineData("osx", Architecture.X64, "BBDownT_osx-x64")]
    [InlineData("osx", Architecture.Arm64, "BBDownT_osx-arm64")]
    public void GetAssetName_MapsSupportedPlatforms(
        string platform, Architecture architecture, string expected)
    {
        Assert.Equal(expected, BBDownTSelfUpdater.GetAssetName(platform, architecture));
    }

    [Theory]
    [InlineData("freebsd", Architecture.X64)]
    [InlineData("win", Architecture.X86)]
    public void GetAssetName_RejectsUnsupportedPlatformsAndArchitectures(
        string platform, Architecture architecture)
    {
        Assert.Throws<PlatformNotSupportedException>(
            () => BBDownTSelfUpdater.GetAssetName(platform, architecture));
    }

    [Theory]
    [InlineData("/tools/bbd", "/tools/BBDownT.dll", true, (int)BBDownTSelfUpdater.InstallationKind.DotnetTool)]
    [InlineData("/tools/bbd", "", true, (int)BBDownTSelfUpdater.InstallationKind.DotnetTool)]
    [InlineData("/tools/bbd", "/tools/BBDownT.dll", false, (int)BBDownTSelfUpdater.InstallationKind.Managed)]
    [InlineData(null, "", false, (int)BBDownTSelfUpdater.InstallationKind.Managed)]
    [InlineData("/usr/bin/dotnet", "", false, (int)BBDownTSelfUpdater.InstallationKind.Managed)]
    [InlineData("/sdk/DOTNET.exe", "", false, (int)BBDownTSelfUpdater.InstallationKind.Managed)]
    [InlineData("/opt/renamed/bbd", "", false, (int)BBDownTSelfUpdater.InstallationKind.Standalone)]
    public void DetectInstallation_DistinguishesToolsManagedHostsAndStandaloneBinaries(
        string? executable,
        string assemblyLocation,
        bool hasToolSettings,
        int expected)
    {
        Assert.Equal(
            (BBDownTSelfUpdater.InstallationKind)expected,
            BBDownTSelfUpdater.DetectInstallation(executable, assemblyLocation, hasToolSettings));
    }

    [Fact]
    public async Task UpdateAsync_DownloadsVerifiesAndInstallsKeepingOnlyTheNewBackupAndMode()
    {
        using var fixture = new UpdateFixture();
        var olderBackup = fixture.CreateFile("bbd.backup-1.0.0-" + new string('a', 32));
        var unrelated = fixture.CreateFile("bbd.backup-manual");
        var originalMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead;
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(fixture.TargetPath, originalMode);
        using var handler = new RouteHandler(request => request.RequestUri!.AbsoluteUri switch
        {
            BBDownTSelfUpdater.ReleasesUrl => JsonResponse(CreateReleaseJson()),
            var url when url == ExpectedAssetUrl => BinaryResponse(UpdatedBytes),
            var url => throw new InvalidOperationException($"Unexpected URL: {url}")
        });
        using var client = new HttpClient(handler);
        var verifyCalls = 0;

        var result = await BBDownTSelfUpdater.UpdateAsync(
            client,
            fixture.TargetPath,
            CurrentVersion,
            AssetName,
            windows: false,
            async (staged, expected, cancellationToken) =>
            {
                verifyCalls++;
                Assert.Equal(LatestVersion, expected);
                Assert.StartsWith(fixture.TargetPath + ".update-", staged, StringComparison.Ordinal);
                Assert.Equal(UpdatedBytes, await File.ReadAllBytesAsync(staged, cancellationToken));
            });

        var completed = Assert.IsType<(BBDownTSelfUpdater.ReleaseVersion Version, string BackupPath)>(result);
        fixture.SuccessBackupPath = completed.BackupPath;
        Assert.Equal(LatestVersion, completed.Version);
        Assert.Equal(1, verifyCalls);
        Assert.Equal(UpdatedBytes, await File.ReadAllBytesAsync(fixture.TargetPath));
        Assert.Equal(OriginalBytes, await File.ReadAllBytesAsync(completed.BackupPath));
        Assert.Equal(
            new[] { BBDownTSelfUpdater.ReleasesUrl, ExpectedAssetUrl },
            handler.RequestedUrls);
        Assert.False(File.Exists(fixture.LockPath));
        Assert.False(File.Exists(olderBackup));
        Assert.True(File.Exists(unrelated));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(originalMode, File.GetUnixFileMode(fixture.TargetPath));
            Assert.Equal(originalMode, File.GetUnixFileMode(completed.BackupPath));
        }
    }

    [Theory]
    [InlineData("digest-mismatch")]
    [InlineData("truncated")]
    [InlineData("probe-version-mismatch")]
    [InlineData("network-failure")]
    public async Task UpdateAsync_FailuresPreserveTheOriginalAndRemoveTemporaryFiles(string scenario)
    {
        using var fixture = new UpdateFixture();
        string digest = scenario == "digest-mismatch"
            ? new string('0', 64)
            : Sha256(UpdatedBytes);
        long size = scenario == "truncated" ? UpdatedBytes.Length + 1 : UpdatedBytes.Length;
        var releaseJson = CreateReleaseJson(size: size, digest: "sha256:" + digest);
        using var handler = new RouteHandler(request =>
        {
            string url = request.RequestUri!.AbsoluteUri;
            if (url == BBDownTSelfUpdater.ReleasesUrl)
                return JsonResponse(releaseJson);
            if (url != ExpectedAssetUrl)
                throw new InvalidOperationException($"Unexpected URL: {url}");
            if (scenario == "network-failure")
                throw new HttpRequestException("offline failure");
            return scenario == "truncated"
                ? StreamingBinaryResponse(UpdatedBytes)
                : BinaryResponse(UpdatedBytes);
        });
        using var client = new HttpClient(handler);

        await Assert.ThrowsAnyAsync<Exception>(() => BBDownTSelfUpdater.UpdateAsync(
            client,
            fixture.TargetPath,
            CurrentVersion,
            AssetName,
            windows: false,
            (_, expected, _) => scenario == "probe-version-mismatch"
                ? Task.FromException(new InvalidDataException(
                    $"probe reported 1.5.0 instead of {expected}"))
                : Task.CompletedTask));

        Assert.Equal(OriginalBytes, await File.ReadAllBytesAsync(fixture.TargetPath));
        Assert.Equal(new[] { fixture.TargetPath }, Directory.GetFiles(fixture.WorkingDirectory));
        Assert.Equal(
            new[] { BBDownTSelfUpdater.ReleasesUrl, ExpectedAssetUrl },
            handler.RequestedUrls);
    }

    [Fact]
    public async Task UpdateAsync_ExistingLockPreventsConcurrentUpdateAndPreservesTheLock()
    {
        using var fixture = new UpdateFixture();
        await using (var existingLock = new FileStream(
            fixture.LockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            using var handler = new RouteHandler(request =>
                request.RequestUri!.AbsoluteUri == BBDownTSelfUpdater.ReleasesUrl
                    ? JsonResponse(CreateReleaseJson())
                    : throw new InvalidOperationException("Asset download must not start while locked"));
            using var client = new HttpClient(handler);

            await Assert.ThrowsAnyAsync<IOException>(() => BBDownTSelfUpdater.UpdateAsync(
                client,
                fixture.TargetPath,
                CurrentVersion,
                AssetName,
                windows: false,
                (_, _, _) => Task.CompletedTask));

            Assert.True(File.Exists(fixture.LockPath));
            Assert.Equal(OriginalBytes, await File.ReadAllBytesAsync(fixture.TargetPath));
            Assert.Equal(new[] { BBDownTSelfUpdater.ReleasesUrl }, handler.RequestedUrls);
            Assert.Equal(2, Directory.GetFiles(fixture.WorkingDirectory).Length);
        }
    }

    [Fact]
    public async Task Install_WindowsSuccessMovesTheOldTargetToBackupAndInstallsTheStagedFile()
    {
        using var fixture = new UpdateFixture();
        await File.WriteAllBytesAsync(fixture.StagedPath, UpdatedBytes);

        BBDownTSelfUpdater.Install(
            fixture.StagedPath,
            fixture.TargetPath,
            fixture.BackupPath,
            windows: true);

        Assert.Equal(UpdatedBytes, await File.ReadAllBytesAsync(fixture.TargetPath));
        Assert.Equal(OriginalBytes, await File.ReadAllBytesAsync(fixture.BackupPath));
        Assert.False(File.Exists(fixture.StagedPath));
        Assert.Equal(2, Directory.GetFiles(fixture.WorkingDirectory).Length);
    }

    [Fact]
    public async Task Install_WindowsRollbackRestoresTheTargetWhenTheStagedFileIsMissing()
    {
        using var fixture = new UpdateFixture();

        Assert.Throws<FileNotFoundException>(() => BBDownTSelfUpdater.Install(
            fixture.StagedPath,
            fixture.TargetPath,
            fixture.BackupPath,
            windows: true));

        Assert.Equal(OriginalBytes, await File.ReadAllBytesAsync(fixture.TargetPath));
        Assert.False(File.Exists(fixture.BackupPath));
        Assert.False(File.Exists(fixture.StagedPath));
        Assert.Equal(new[] { fixture.TargetPath }, Directory.GetFiles(fixture.WorkingDirectory));
    }

    [Fact]
    public async Task Install_UnixReplacementFailureRetainsTheOriginalAndItsBackup()
    {
        using var fixture = new UpdateFixture();

        var exception = Assert.Throws<IOException>(() => BBDownTSelfUpdater.Install(
            fixture.StagedPath,
            fixture.TargetPath,
            fixture.BackupPath,
            windows: false));

        Assert.Contains(fixture.BackupPath, exception.Message);
        Assert.Equal(OriginalBytes, await File.ReadAllBytesAsync(fixture.TargetPath));
        Assert.Equal(OriginalBytes, await File.ReadAllBytesAsync(fixture.BackupPath));
        Assert.False(File.Exists(fixture.StagedPath));
        Assert.Equal(2, Directory.GetFiles(fixture.WorkingDirectory).Length);
    }

    private static string ExpectedAssetUrl =>
        $"https://github.com/LOVAHE/BBDownT/releases/download/v2.0.0/{AssetName}";

    private static BBDownTSelfUpdater.ReleaseVersion Parse(string text)
        => BBDownTSelfUpdater.ReleaseVersion.TryParse(text, out var version) ? version : throw new FormatException(text);

    private static string CreateReleaseJson(
        string tag = "v2.0.0",
        bool draft = false,
        bool prerelease = false,
        int assetCount = 1,
        long? size = null,
        string? digest = null,
        string? downloadUrl = null)
        => "[" + CreateRelease(tag, draft, prerelease, assetCount, size, digest, downloadUrl) + "]";

    private static string CreateRelease(
        string tag,
        bool draft = false,
        bool prerelease = false,
        int assetCount = 1,
        long? size = null,
        string? digest = null,
        string? downloadUrl = null)
    {
        var assets = Enumerable.Range(0, assetCount).Select(_ => new
        {
            name = AssetName,
            state = "uploaded",
            size = size ?? UpdatedBytes.Length,
            digest = digest ?? "sha256:" + Sha256(UpdatedBytes),
            browser_download_url = downloadUrl ?? $"https://github.com/LOVAHE/BBDownT/releases/download/{tag}/{AssetName}"
        });
        return JsonSerializer.Serialize(new
        {
            draft,
            prerelease,
            tag_name = tag,
            assets
        });
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage BinaryResponse(byte[] bytes) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(bytes)
    };

    private static HttpResponseMessage StreamingBinaryResponse(byte[] bytes) => new(HttpStatusCode.OK)
    {
        Content = new UnknownLengthContent(bytes)
    };

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class RouteHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        internal List<string> RequestedUrls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestedUrls.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(route(request));
        }
    }

    private sealed class UpdateFixture : IDisposable
    {
        private readonly bool _createdFixtureRoot;
        private readonly List<string> _extraFiles = [];

        internal UpdateFixture()
        {
            FixtureRoot = Path.Combine(AppContext.BaseDirectory, ".self-update-test-fixture");
            _createdFixtureRoot = !Directory.Exists(FixtureRoot);
            Directory.CreateDirectory(FixtureRoot);
            WorkingDirectory = Path.Combine(FixtureRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(WorkingDirectory);
            TargetPath = Path.Combine(WorkingDirectory, "bbd");
            LockPath = TargetPath + ".update.lock";
            StagedPath = Path.Combine(WorkingDirectory, "bbd.staged");
            BackupPath = Path.Combine(WorkingDirectory, "bbd.backup");
            File.WriteAllBytes(TargetPath, OriginalBytes);
        }

        internal string FixtureRoot { get; }
        internal string WorkingDirectory { get; }
        internal string TargetPath { get; }
        internal string LockPath { get; }
        internal string StagedPath { get; }
        internal string BackupPath { get; }
        internal string? SuccessBackupPath { get; set; }

        internal string CreateFile(string name)
        {
            string path = Path.Combine(WorkingDirectory, name);
            File.WriteAllBytes(path, OriginalBytes);
            _extraFiles.Add(path);
            return path;
        }

        public void Dispose()
        {
            foreach (var path in _extraFiles)
                DeleteFile(path);
            DeleteFile(TargetPath);
            DeleteFile(LockPath);
            DeleteFile(StagedPath);
            DeleteFile(BackupPath);
            if (SuccessBackupPath is not null)
                DeleteFile(SuccessBackupPath);
            Directory.Delete(WorkingDirectory, recursive: false);
            if (_createdFixtureRoot)
                Directory.Delete(FixtureRoot, recursive: false);
        }

        private static void DeleteFile(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
