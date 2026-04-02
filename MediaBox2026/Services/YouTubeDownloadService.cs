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
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("YouTube download service waiting for Telegram readiness...");
        await state.WaitForTelegramReadyAsync(ct);
        await Task.Delay(TimeSpan.FromSeconds(15), ct);
        logger.LogInformation("YouTube download service started");

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
        logger.LogInformation("Starting YouTube download for \"{Title}\"...", source.MatchTitle);

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
        process.Start();

        // Kill the process tree if the application is shutting down
        using var killReg = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(true); }
            catch { }
        });

        var output = await process.StandardOutput.ReadToEndAsync(ct);
        var error = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode == 0)
        {
            logger.LogInformation("YouTube download completed: \"{Title}\"", source.MatchTitle);
            state.LastNewsDownload = DateTime.Now;
            state.AddActivity($"YouTube downloaded: {source.MatchTitle}");

            await catalog.ScanAllAsync(ct);
            await telegram.SendMessageAsync($"📹 YouTube downloaded: {source.MatchTitle}", ct);
        }
        else
        {
            logger.LogWarning("yt-dlp exited with code {Code}: {Error}", process.ExitCode, error);
            if (!string.IsNullOrWhiteSpace(error))
                await telegram.SendMessageAsync($"⚠️ YouTube issue ({source.MatchTitle}): {error[..Math.Min(error.Length, 200)]}", ct);
        }
    }
}
