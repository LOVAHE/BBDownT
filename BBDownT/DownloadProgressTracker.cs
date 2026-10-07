using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace BBDownT;

internal readonly record struct DownloadProgressUpdate(
    long CompletedBytes, long TransferredBytes, bool Verifying = false,
    long VerifiedBytes = 0, long VerificationLength = 0)
{
    internal long TotalLength { get; init; }
}

// Retained bytes affect completion, while only received bytes affect speed.
// Verification has its own counter so comparing a prefix does not restart the
// download percentage or make an old checkpoint appear to arrive over the wire.
internal sealed class DownloadProgressTracker
{
    private readonly object gate = new();
    private long totalLength;
    private readonly long[] rangeLengths;
    private readonly long[] completed;
    private readonly long[] verified;
    private readonly long[] verificationLengths;
    private readonly bool[] verifying;
    private readonly Action<double, long, string?> render;
    private readonly Action<string> log;
    private long transferred;
    private bool verificationLogged;

    internal DownloadProgressTracker(long totalLength, IReadOnlyList<long> rangeLengths,
        Action<double, long, string?> render, Action<string> log)
    {
        this.totalLength = totalLength;
        this.rangeLengths = rangeLengths.ToArray();
        completed = new long[rangeLengths.Count];
        verified = new long[rangeLengths.Count];
        verificationLengths = new long[rangeLengths.Count];
        verifying = new bool[rangeLengths.Count];
        this.render = render;
        this.log = log;
    }

    internal void Restore(int index, long bytes, bool needsVerification)
    {
        lock (gate)
        {
            completed[index] = Math.Clamp(bytes, 0, rangeLengths[index]);
            verifying[index] = needsVerification && completed[index] > 0;
            verificationLengths[index] = verifying[index] ? completed[index] : 0;
            verified[index] = 0;
        }
    }

    internal void ReportRestored()
    {
        lock (gate)
        {
            var retained = completed.Sum();
            if (retained > 0)
            {
                var percent = totalLength > 0 ? 100.0 * retained / totalLength : 0;
                log($"已恢复下载进度：{BBDownTUtil.FormatFileSize(retained)}"
                    + (totalLength > 0 ? $" / {BBDownTUtil.FormatFileSize(totalLength)} ({percent.ToString("0.00", CultureInfo.InvariantCulture)}%)" : ""));
            }
            Render();
        }
    }

    internal void Report(int index, DownloadProgressUpdate update)
    {
        lock (gate)
        {
            if (totalLength <= 0 && update.TotalLength > 0) totalLength = update.TotalLength;
            completed[index] = Math.Clamp(update.CompletedBytes, 0, rangeLengths[index]);
            transferred = checked(transferred + Math.Max(0, update.TransferredBytes));
            if (update.Verifying || update.VerificationLength > 0)
            {
                // A retry can compare a shorter checkpoint after replacing a bad prefix.
                verificationLengths[index] = Math.Clamp(update.VerificationLength, 0, rangeLengths[index]);
                verified[index] = update.Verifying
                    ? Math.Clamp(update.VerifiedBytes, 0, verificationLengths[index])
                    : verificationLengths[index];
            }
            else if (verifying[index])
            {
                // A queued part was reused, or its first compared byte mismatched.
                verificationLengths[index] = 0;
                verified[index] = 0;
            }
            // Ordinary download updates retain finished verification in the aggregate.
            verifying[index] = update.Verifying && verificationLengths[index] > 0;
            Render();
        }
    }

    private void Render()
    {
        string? status = null;
        if (verifying.Any(value => value))
        {
            if (!verificationLogged)
            {
                log("正在校验已有数据...");
                verificationLogged = true;
            }
            var length = verificationLengths.Sum();
            var percent = length > 0 ? 100.0 * verified.Sum() / length : 0;
            status = $"校验 {percent.ToString("0.00", CultureInfo.InvariantCulture)}%";
        }
        render(totalLength > 0 ? (double)completed.Sum() / totalLength : 0, transferred, status);
    }
}
