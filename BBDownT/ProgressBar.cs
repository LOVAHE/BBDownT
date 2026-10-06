using System;
using System.IO;
using System.Text;
using System.Threading;
using BBDownT.Core;

/**
 * From https://gist.github.com/DanielSWolf/0ab6a96899cc5377bf54
 */
namespace BBDownT;

class ProgressBar : IDisposable, IProgress<double>
{
	private const int blockCount = 40;
	private readonly TimeSpan animationInterval = TimeSpan.FromSeconds(1.0 / 8);
	private const string animation = @"|/-\";

	private readonly Timer timer;
	private readonly Logger.ProgressLine? progressLine;
	private readonly bool runTimers;
	private readonly int? terminalColumns;

	private double currentProgress = 0;
	private volatile bool disposed = false;
	private int animationIndex = 0;

	//速度计算
	private readonly TimeSpan speedCalcInterval = TimeSpan.FromSeconds(1);
	private long lastDownloadedBytes = 0;
	private long downloadedBytes = 0;
	private string speedString = "";
	private string? statusText;
	private readonly Timer speedTimer;

	//服务器模式使用，更新下载任务的进度
	private DownloadTask? RelatedTask = null;

	public ProgressBar(DownloadTask? task = null)
		: this(task, null, Console.IsOutputRedirected, runTimers: true, terminalColumns: null)
	{
	}

	// In-memory display tests advance the normal rendering path by hand,
	// without scheduling timers or relying on a terminal cursor.
	internal ProgressBar(TextWriter output, bool outputRedirected = false, int terminalColumns = 0)
		: this(null, output, outputRedirected, runTimers: false, terminalColumns: terminalColumns)
	{
	}

	private ProgressBar(DownloadTask? task, TextWriter? output, bool outputRedirected, bool runTimers, int? terminalColumns)
	{
		this.runTimers = runTimers;
		this.terminalColumns = terminalColumns;
		timer = new Timer(TimerHandler);
		speedTimer = new Timer(SpeedTimerHandler);
		if (task is not null) RelatedTask = task;
		if (!outputRedirected) progressLine = Logger.RegisterProgressLine(output);
		// A progress bar is only for temporary display in a console window.
		// If the console output is redirected to a file, draw nothing.
		// Otherwise, we'll end up with a lot of garbage in the target file.
		// However, if this progressbar is for a server download task,
		// we still need it to report progress no matter where stdout is redirected.
		// The prevention of writing garbage should be controlled on the methods do the actual writing.
		if (runTimers && (!outputRedirected || RelatedTask is not null))
		{
			ResetTimer();
			ResetSpeedTimer();

		}
	}

	public void Report(double value)
	{
		value = NormalizeProgress(value);
		Interlocked.Exchange(ref currentProgress, value);
	}

	public void Report(double value, long bytesCount)
	{
		value = NormalizeProgress(value);
		Interlocked.Exchange(ref currentProgress, value);
		Interlocked.Exchange(ref downloadedBytes, bytesCount);
	}

	internal void ReportDownload(double value, long transferredBytes, string? status = null)
	{
		Report(value, transferredBytes);
		Interlocked.Exchange(ref statusText, status);
		RelatedTask?.ReportProgress(NormalizeProgress(value));
	}

	internal static double NormalizeProgress(double value)
	{
		return double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
	}

	private void SpeedTimerHandler(object? state)
	{
		lock (speedTimer)
		{
			if (disposed) return;

			UpdateSpeed();

			if (runTimers) ResetSpeedTimer();
		}
	}

	private void UpdateSpeed()
	{
		var received = Interlocked.Read(ref downloadedBytes);
		var delta = Math.Max(0, received - lastDownloadedBytes);
		speedString = " - " + BBDownTUtil.FormatFileSize(delta) + "/s";
		lastDownloadedBytes = received;
		RelatedTask?.ReportDownloadedBytes(delta);
	}

	private void TimerHandler(object? state)
	{
		lock (timer)
		{
			if (disposed) return;

			string text = FormatDisplayText(currentProgress, speedString, statusText,
				animation[animationIndex++ % animation.Length], GetTerminalColumns());
			UpdateText(text);
			if (RelatedTask is not null) 
			{
					RelatedTask.ReportProgress(currentProgress);
			}

			if (runTimers) ResetTimer();
		}
	}

	private int GetTerminalColumns()
	{
		if (terminalColumns.HasValue) return terminalColumns.Value;
		if (Console.IsOutputRedirected) return 0;
		try
		{
			return Console.WindowWidth;
		}
		catch (IOException) { return 0; }
		catch (PlatformNotSupportedException) { return 0; }
	}

	internal static string FormatDisplayText(double progress, string speed, string? status, char frame, int columns)
	{
		int padding = 28;
		int blocks = blockCount;
		string percent = string.Format(" {0,3:0.00}% {1}", progress * 100, frame);
		string phase = status is null ? "" : " - " + status;
		if (columns > 0)
		{
			// Keep the final terminal column free: reaching it can trigger an
			// automatic wrap that a later backspace cannot safely reverse.
			int available = columns - 1;
			int fixedColumns = 2 + Logger.ProgressLine.GetDisplayWidth(percent.AsSpan());
			int suffixColumns = Logger.ProgressLine.GetDisplayWidth(speed.AsSpan())
				+ Logger.ProgressLine.GetDisplayWidth(phase.AsSpan());
			int overflow = Math.Max(0, padding + blocks + fixedColumns + suffixColumns - available);
			int removed = Math.Min(padding, overflow);
			padding -= removed;
			overflow -= removed;
			removed = Math.Min(blocks - 1, overflow);
			blocks -= removed;
			overflow -= removed;
			if (overflow > 0)
			{
				speed = "";
				int phaseBudget = available - padding - blocks - fixedColumns;
				if (Logger.ProgressLine.GetDisplayWidth(phase.AsSpan()) > phaseBudget)
					phase = phaseBudget > 3 ? " - " + TruncateDisplayText(status ?? "", phaseBudget - 3) : "";
			}
		}

		int filled = (int)(progress * blocks);
		return new string(' ', padding) + "[" + new string('#', filled)
			+ new string('-', blocks - filled) + "]" + percent + speed + phase;
	}

	private static string TruncateDisplayText(string text, int columns)
	{
		if (Logger.ProgressLine.GetDisplayWidth(text.AsSpan()) <= columns) return text;
		var shortened = new StringBuilder();
		int used = 0;
		foreach (var character in text.EnumerateRunes())
		{
			int width = Logger.ProgressLine.GetDisplayWidth(character);
			if (used + width > columns - 1) break;
			shortened.Append(character.ToString());
			used += width;
		}
		return shortened.Append('…').ToString();
	}

	private void UpdateText(string text)
	{
		progressLine?.Update(text);
	}

	internal void RefreshDisplay() => TimerHandler(null);

	private void ResetTimer()
	{
		timer.Change(animationInterval, TimeSpan.FromMilliseconds(-1));
	}

	private void ResetSpeedTimer()
	{
		speedTimer.Change(speedCalcInterval, TimeSpan.FromMilliseconds(-1));
	}

	public void Dispose()
	{
		lock (timer)
		{
			if (disposed) return;
			disposed = true;
			progressLine?.Dispose();
			timer.Dispose();
		}
		lock (speedTimer)
		{
			UpdateSpeed();
			speedTimer.Dispose();
		}
	}
}
