using System.Diagnostics;
using MediaBox2026.Models;
using Microsoft.Extensions.Options;

namespace MediaBox2026.Services;

public class YouTubeDownloadService(
    MediaCatalogService catalog,
    ITelegramNotifier telegram,
    MediaBoxState state,
    IOptionsMonitor<MediaBoxSettings> settings,
    ILogger<YouTubeDownloadService> logger) : BackgroundService
{
    private int _consecutiveFailures = 0;
    private const int MaxConsecutiveFailures = 5;
    private const int ProcessTimeoutMinutes = 30;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("📹 YouTube download service waiting for Telegram readiness...");

        try
        {
            await state.WaitForTelegramReadyAsync(ct);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("YouTube download service cancelled during Telegram wait");
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(15), ct);

        // Validate yt-dlp is installed
        if (!await ValidateYtDlpInstalledAsync(ct))
        {
            logger.LogCritical("❌ yt-dlp is not installed or not in PATH. YouTube download service will not function.");
            return;
        }

        logger.LogInformation("🚀 YouTube download service started");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var sources = settings.CurrentValue.NewsSources;
                if (sources.Count == 0)
                {
                    logger.LogWarning("No news sources configured, sleeping 1 hour");
                    await Task.Delay(TimeSpan.FromHours(1), ct);
                    continue;
                }

                var (nextSource, delay) = GetNextScheduled(sources);
                logger.LogInformation("Next YouTube download: \"{Title}\" at {Time} (in {Delay})",
                    nextSource.MatchTitle, DateTime.Now.Add(delay).ToString("HH:mm"), delay);
                await Task.Delay(delay, ct);

                await DownloadNewsAsync(nextSource, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "YouTube download error");
                await Task.Delay(TimeSpan.FromMinutes(30), ct);
            }
        }
    }

    private static (NewsSource Source, TimeSpan Delay) GetNextScheduled(List<NewsSource> sources)
    {
        var now = DateTime.Now;
        NewsSource? best = null;
        var bestDelay = TimeSpan.MaxValue;

        foreach (var src in sources)
        {
            if (!TimeOnly.TryParse(src.DownloadTime, out var target))
                continue;

            var next = now.Date.Add(target.ToTimeSpan());
            if (next <= now) next = next.AddDays(1);
            var delay = next - now;

            if (delay < bestDelay)
            {
                bestDelay = delay;
                best = src;
            }
        }

        return best is not null
            ? (best, bestDelay)
            : (sources[0], TimeSpan.FromHours(1));
    }

    private async Task DownloadNewsAsync(NewsSource source, CancellationToken ct)
    {
        var config = settings.CurrentValue;
        var startTime = DateTime.UtcNow;
        logger.LogInformation("📥 Starting YouTube download for \"{Title}\"...", source.MatchTitle);
        logger.LogInformation("🌐 Source URL: {Url}", source.Url);

        // Validate download path exists
        if (!Directory.Exists(config.YouTubePath))
        {
            logger.LogWarning("📁 YouTube path does not exist, creating: {Path}", config.YouTubePath);
            Directory.CreateDirectory(config.YouTubePath);
        }

        var args = string.Join(" ",
            "--js-runtimes node",
            "--ignore-errors",
            $"--download-archive \"{config.YtDlpArchivePath}\"",
            "--no-overwrites",
            "--dateafter today-3days",
            "--playlist-end 20",
            "--retries 5",
            "--fragment-retries 5",
            $"--match-title \"{source.MatchTitle}\"",
            $"-o \"{config.YouTubePath}/%(uploader)s/%(upload_date)s - %(title)s.%(ext)s\"",
            "-f \"best[height<=720]\"",
            source.Url);

        var psi = new ProcessStartInfo
        {
            FileName = "yt-dlp",
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };

        logger.LogDebug("🚀 Executing: yt-dlp {Args}", args);
        process.Start();

        // Kill the process tree if the application is shutting down or timeout
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(ProcessTimeoutMinutes));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        using var killReg = linkedCts.Token.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    logger.LogWarning("⏹️ Killing yt-dlp process (timeout or cancellation)");
                    process.Kill(true);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to kill yt-dlp process");
            }
        });

        var output = await process.StandardOutput.ReadToEndAsync(linkedCts.Token);
        var error = await process.StandardError.ReadToEndAsync(linkedCts.Token);
        await process.WaitForExitAsync(linkedCts.Token);

        var duration = DateTime.UtcNow - startTime;

        if (linkedCts.Token.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            logger.LogError("⏱️ yt-dlp process timed out after {Minutes} minutes", ProcessTimeoutMinutes);
            await telegram.SendMessageAsync($"⚠️ YouTube download timed out: {source.MatchTitle}", ct);
            return;
        }

        if (process.ExitCode == 0)
        {
            logger.LogInformation("✅ YouTube download completed in {Duration:F1}s: \"{Title}\"", duration.TotalSeconds, source.MatchTitle);
            if (!string.IsNullOrWhiteSpace(output))
            {
                logger.LogDebug("yt-dlp output: {Output}", output);
            }

            state.LastNewsDownload = DateTime.Now;
            state.AddActivity($"YouTube downloaded: {source.MatchTitle}");

            logger.LogInformation("📺 Triggering media scan...");
            await catalog.ScanAllAsync(ct);
            await telegram.SendMessageAsync($"📹 YouTube downloaded: {source.MatchTitle}", ct);
        }
        else
        {
            logger.LogWarning("❌ yt-dlp exited with code {Code} after {Duration:F1}s", process.ExitCode, duration.TotalSeconds);
            if (!string.IsNullOrWhiteSpace(error))
            {
                logger.LogError("yt-dlp error output: {Error}", error);
                await telegram.SendMessageAsync($"⚠️ YouTube download issue ({source.MatchTitle}): {error[..Math.Min(error.Length, 200)]}", ct);
            }
            if (!string.IsNullOrWhiteSpace(output))
            {
                logger.LogDebug("yt-dlp stdout: {Output}", output);
            }
        }
    }

    private async Task<bool> ValidateYtDlpInstalledAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "yt-dlp",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            process.Start();
            var version = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode == 0)
            {
                logger.LogInformation("✅ yt-dlp version: {Version}", version.Trim());
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "❌ Failed to validate yt-dlp installation");
            return false;
        }
    }
}
