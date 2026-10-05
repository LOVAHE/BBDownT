using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace BBDownT;

internal sealed record DownloadResourceMetadata(long TotalLength, DownloadResumeValidator Validator);

internal sealed record DownloadResumeState(
    string ResourceIdentity, string SourceUriHash, long FromPosition, long? ToPosition, long TotalLength,
    bool Complete, long LocalLength, string LocalSha256, DownloadResumeValidator Validator)
{
    public int Version { get; init; } = 1;
    internal long RangeLength => (ToPosition ?? TotalLength - 1) - FromPosition + 1;

    internal static string Scope(string url, string? identity)
        => identity ?? SourceHash(url);

    internal static string SourceHash(string url)
    {
        var fragment = url.IndexOf('#');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fragment < 0 ? url : url[..fragment])));
    }

    internal bool MatchesRange(string identity, long from, long? to, long? total = null)
        => Version == 1 && ResourceIdentity == identity && FromPosition == from && ToPosition == to
            && (total is null || TotalLength == total) && LocalLength >= 0 && LocalLength <= RangeLength
            && (!Complete || LocalLength == RangeLength);

    internal async Task<bool> MatchesFileAsync(string path)
    {
        if (!File.Exists(path)) return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await MatchesStreamAsync(stream);
    }

    internal async Task<bool> MatchesStreamAsync(Stream stream)
    {
        if (stream.Length < LocalLength || (Complete && stream.Length != LocalLength) || LocalSha256.Length != 64) return false;
        stream.Position = 0;
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[262144];
        var remaining = LocalLength;
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)));
            if (read == 0) return false;
            digest.AppendData(buffer, 0, read);
            remaining -= read;
        }
        var hash = Convert.ToHexString(digest.GetHashAndReset());
        stream.Position = stream.Length;
        return hash.Equals(LocalSha256, StringComparison.Ordinal);
    }

    internal static DownloadResumeState? Parse(string text)
    {
        try
        {
            var state = JsonSerializer.Deserialize(text, DownloadResumeStateJsonContext.Default.DownloadResumeState);
            return state is { Version: 1, ResourceIdentity: not null, SourceUriHash: not null, LocalSha256: not null, Validator: not null }
                && state.FromPosition >= 0 && state.TotalLength > state.FromPosition
                && state.RangeLength > 0 && state.RangeLength <= state.TotalLength - state.FromPosition
                && state.LocalLength >= 0 && state.LocalLength <= state.RangeLength
                && (!state.Complete || state.LocalLength == state.RangeLength) ? state : null;
        }
        catch (JsonException) { return null; }
    }

    internal static async Task<DownloadResumeState?> LoadAsync(string path)
        => File.Exists(path) ? Parse(await File.ReadAllTextAsync(path)) : null;

    internal async Task SaveAsync(string path, string? restrictedOutputRoot = null)
    {
        path = OutputPathPolicy.ResolveArtifact(path, restrictedOutputRoot);
        var temporary = OutputPathPolicy.ResolveArtifact(path + ".writing-" + Guid.NewGuid().ToString("N"), restrictedOutputRoot);
        try
        {
            await File.WriteAllTextAsync(temporary,
                JsonSerializer.Serialize(this, DownloadResumeStateJsonContext.Default.DownloadResumeState));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

[JsonSerializable(typeof(DownloadResumeState))]
internal partial class DownloadResumeStateJsonContext : JsonSerializerContext { }
