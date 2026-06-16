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
    private readonly SemaphoreSlim _manualTriggerLock = new(1, 1);

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
                await RunOnceAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "YouTube download error");
                await Task.Delay(TimeSpan.FromMinutes(30), ct);
            }
        }
    }

    /// <summary>
    /// Runs one scheduling cycle: checks pause state, determines the next due source (sleeping
    /// until it's due), re-checks pause state, and downloads it. This is the per-cycle work that
    /// ExecuteAsync's loop runs continuously; it's a PURE extraction of that loop body (including
    /// its own internal scheduling sleeps, which are this service's wait-for-next-source logic,
    /// not a generic outer interval delay — there is no separate trailing delay in this service).
    /// </summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        var config = settings.CurrentValue;

        if (state.YouTubeTemporarilyPaused)
        {
            logger.LogInformation("⏸️ YouTube downloads temporarily paused via Telegram. Sleeping 5 minutes.");
            await Task.Delay(TimeSpan.FromMinutes(5), ct);
            return;
        }

        if (config.YouTubeDownloadPaused)
        {
            logger.LogInformation("⏸️ YouTube downloads disabled in settings (YouTubeDownloadPaused=true). Sleeping 1 hour.");
            await Task.Delay(TimeSpan.FromHours(1), ct);
            return;
        }

        var sources = config.NewsSources;
        if (sources.Count == 0)
        {
            logger.LogWarning("No news sources configured, sleeping 1 hour");
            await Task.Delay(TimeSpan.FromHours(1), ct);
            return;
        }

        var next = GetNextScheduled(sources);
        if (next is null)
        {
            logger.LogInformation("⏸️ All YouTube sources are paused. Sleeping 5 minutes.");
            await Task.Delay(TimeSpan.FromMinutes(5), ct);
            return;
        }

        var (nextSource, delay) = next.Value;
        logger.LogInformation("Next YouTube download: \"{Title}\" at {Time} (in {Delay})",
            nextSource.MatchTitle, DateTime.Now.Add(delay).ToString("HH:mm"), delay);
        await Task.Delay(delay, ct);

        if (state.YouTubeTemporarilyPaused || settings.CurrentValue.YouTubeDownloadPaused)
        {
            logger.LogInformation("⏸️ YouTube downloads paused — skipping scheduled download for \"{Title}\"", nextSource.MatchTitle);
            return;
        }

        // Re-check per-source pause after the long delay
        var currentSource = settings.CurrentValue.NewsSources
            .FirstOrDefault(s => s.MatchTitle == nextSource.MatchTitle);
        if (currentSource?.Paused == true || state.IsSourceTemporarilyPaused(nextSource.MatchTitle))
        {
            logger.LogInformation("⏸️ Source paused after delay — skipping: \"{Title}\"", nextSource.MatchTitle);
            return;
        }

        await DownloadNewsAsync(nextSource, ct);
    }

    private (NewsSource Source, TimeSpan Delay)? GetNextScheduled(List<NewsSource> sources)
    {
        var now = DateTime.Now;
        NewsSource? best = null;
        var bestDelay = TimeSpan.MaxValue;

        foreach (var src in sources)
        {
            if (src.Paused || state.IsSourceTemporarilyPaused(src.MatchTitle))
            {
                logger.LogDebug("⏭️ Skipping paused source: {Title}", src.MatchTitle);
                continue;
            }

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

        return best is not null ? (best, bestDelay) : null;
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
            "--no-cache-dir",
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

    public async Task<int> TriggerManualDownloadAsync(CancellationToken ct = default)
    {
        if (state.YouTubeTemporarilyPaused)
        {
            logger.LogWarning("⏸️ Manual YouTube download requested but service is temporarily paused");
            return -2;
        }

        if (settings.CurrentValue.YouTubeDownloadPaused)
        {
            logger.LogWarning("⏸️ Manual YouTube download requested but service is disabled in settings");
            return -3;
        }

        if (!await _manualTriggerLock.WaitAsync(0, ct))
        {
            logger.LogWarning("⚠️ Manual YouTube download already in progress");
            return -1;
        }

        try
        {
            var sources = settings.CurrentValue.NewsSources;
            if (sources.Count == 0)
            {
                logger.LogWarning("No news sources configured for manual download");
                return 0;
            }

            logger.LogInformation("🎬 Manual YouTube download triggered for {Count} source(s)", sources.Count);

            int successCount = 0;
            int skippedCount = 0;
            int failCount = 0;
            for (int i = 0; i < sources.Count; i++)
            {
                var source = sources[i];

                if (source.Paused || state.IsSourceTemporarilyPaused(source.MatchTitle))
                {
                    logger.LogInformation("⏭️ Skipping paused source [{Current}/{Total}]: {Title}", i + 1, sources.Count, source.MatchTitle);
                    skippedCount++;
                    continue;
                }

                logger.LogInformation("📥 [{Current}/{Total}] Downloading: {Title} from {Url}", i + 1, sources.Count, source.MatchTitle, source.Url);

                try
                {
                    await DownloadNewsAsync(source, ct);
                    successCount++;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "❌ Failed to download: {Title}", source.MatchTitle);
                    await telegram.SendMessageAsync($"❌ Failed to download: {source.MatchTitle}", ct);
                    failCount++;
                }

                // Delay between downloads to avoid rate limiting (especially for same channel)
                if (i < sources.Count - 1)
                {
                    logger.LogInformation("⏳ Waiting 15 seconds before next download...");
                    await Task.Delay(TimeSpan.FromSeconds(15), ct);
                }
            }

            logger.LogInformation("✅ Manual YouTube download complete: {Success}/{Total} successful, {Skipped} skipped", successCount, sources.Count, skippedCount);

            // Only notify if there was meaningful activity — don't spam when all sources are paused
            if (successCount > 0 || failCount > 0)
            {
                var parts = new List<string>();
                if (successCount > 0) parts.Add($"{successCount} downloaded");
                if (failCount > 0) parts.Add($"{failCount} failed");
                if (skippedCount > 0) parts.Add($"{skippedCount} paused");
                await telegram.SendMessageAsync($"✅ YouTube done: {string.Join(", ", parts)}", ct);
            }

            return successCount;
        }
        finally
        {
            _manualTriggerLock.Release();
        }
    }
}
