using MediaBox2026.Models;
using Microsoft.Extensions.Options;

namespace MediaBox2026.Services;

public class DownloadOrganizerService(
    MediaDatabase db,
    MediaCatalogService catalog,
    TransmissionClient transmission,
    JellyfinClient jellyfin,
    ITelegramNotifier telegram,
    MediaBoxState state,
    IOptionsMonitor<MediaBoxSettings> settings,
    ILogger<DownloadOrganizerService> logger) : BackgroundService
{
    private int _consecutiveFailures = 0;
    private const int MaxConsecutiveFailures = 5;
    private readonly SemaphoreSlim _organizeLock = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("📋 Download organizer waiting for Telegram readiness...");

        try
        {
            await state.WaitForTelegramReadyAsync(ct);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Download organizer cancelled during Telegram wait");
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(45), ct);
        logger.LogInformation("🚀 Download organizer started");
        logger.LogInformation("Check interval: {Minutes} minutes", settings.CurrentValue.DownloadOrganizerMinutes);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                logger.LogInformation("=== Download Organizer Cycle Starting ===");
                if (_consecutiveFailures > 0)
                {
                    logger.LogWarning("⚠️ Consecutive failures: {Count}/{Max}", _consecutiveFailures, MaxConsecutiveFailures);
                }

                var checkStart = DateTime.UtcNow;
                await OrganizeAsync(ct);
                _consecutiveFailures = 0; // Reset on success

                var duration = DateTime.UtcNow - checkStart;
                logger.LogInformation("✅ Download organizer cycle completed in {Duration:F1}s", duration.TotalSeconds);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                logger.LogInformation("🛑 Download organizer shutting down...");
                break;
            }
            catch (IOException ioex)
            {
                _consecutiveFailures++;
                logger.LogError(ioex, "❌ File system error (consecutive failures: {Count}/{Max})", _consecutiveFailures, MaxConsecutiveFailures);
            }
            catch (UnauthorizedAccessException uaex)
            {
                _consecutiveFailures++;
                logger.LogError(uaex, "❌ Access denied error (consecutive failures: {Count}/{Max})", _consecutiveFailures, MaxConsecutiveFailures);
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                logger.LogError(ex, "❌ Download organizer error (consecutive failures: {Count}/{Max})", _consecutiveFailures, MaxConsecutiveFailures);
            }

            if (_consecutiveFailures >= MaxConsecutiveFailures)
            {
                logger.LogCritical("🚨 Download organizer reached max consecutive failures. Increasing retry delay.");
                await Task.Delay(TimeSpan.FromMinutes(settings.CurrentValue.DownloadOrganizerMinutes * 2), ct);
                _consecutiveFailures = 0;
                continue;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(settings.CurrentValue.DownloadOrganizerMinutes), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                logger.LogInformation("🛑 Download organizer shutting down during delay...");
                break;
            }
        }
    }

    private async Task OrganizeAsync(CancellationToken ct)
    {
        // Prevent concurrent organization
        if (!await _organizeLock.WaitAsync(0, ct))
        {
            logger.LogDebug("Organize operation already in progress, skipping this cycle");
            return;
        }

        try
        {
            var config = settings.CurrentValue;

            // Validate paths exist
            if (!Directory.Exists(config.DownloadsPath))
            {
                logger.LogWarning("⚠️ Downloads path does not exist: {Path}", config.DownloadsPath);
                return;
            }

            if (!Directory.Exists(config.TvShowsPath))
            {
                logger.LogWarning("⚠️ TV Shows path does not exist, creating: {Path}", config.TvShowsPath);
                Directory.CreateDirectory(config.TvShowsPath);
            }

            if (!Directory.Exists(config.MoviesPath))
            {
                logger.LogWarning("⚠️ Movies path does not exist, creating: {Path}", config.MoviesPath);
                Directory.CreateDirectory(config.MoviesPath);
            }

            var activeTorrents = await transmission.GetTorrentsAsync(ct);
            var activeNames = activeTorrents
                .Where(t => !t.IsFinished)
                .Select(t => t.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            logger.LogDebug("{Count} active torrents to skip during organization", activeNames.Count);

            var movedAny = false;
            var entries = Directory.GetFileSystemEntries(config.DownloadsPath);
            logger.LogInformation("📋 Found {Count} items in downloads folder", entries.Length);

            var processedCount = 0;
            var skippedCount = 0;

            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                if (activeNames.Contains(name))
                {
                    skippedCount++;
                    logger.LogDebug("Skipping active torrent: {Name}", name);
                    continue;
                }

                try
                {
                    if (Directory.Exists(entry))
                        movedAny |= await ProcessDirectoryAsync(entry, config, ct);
                    else
                        movedAny |= await ProcessFileAsync(entry, config, ct);

                    processedCount++;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "❌ Failed to process: {Entry}", name);
                }
            }

            logger.LogInformation("📊 Organization summary: {Processed} processed, {Skipped} skipped", processedCount, skippedCount);

            if (movedAny)
            {
                logger.LogInformation("📺 Triggering Jellyfin library scan...");
                try
                {
                    await jellyfin.TriggerLibraryScanAsync(ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "⚠️ Jellyfin library scan failed (non-critical)");
                }
            }
        }
        finally
        {
            _organizeLock.Release();
        }
    }

    private async Task<bool> ProcessDirectoryAsync(string dirPath, MediaBoxSettings config, CancellationToken ct)
    {
        var allFiles = Directory.GetFiles(dirPath, "*.*", SearchOption.AllDirectories);
        var hasMediaFiles = false;

        foreach (var file in allFiles)
        {
            if (FileNameParser.IsMediaFile(file) || FileNameParser.IsSubtitleFile(file))
            {
                hasMediaFiles = true;
                await MoveMediaFileAsync(file, config, ct);
            }
            else
            {
                MoveToUnknown(file, config);
            }
        }

        try
        {
            if (hasMediaFiles && Directory.Exists(dirPath) && !Directory.EnumerateFileSystemEntries(dirPath).Any())
                Directory.Delete(dirPath, recursive: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not clean up directory: {Dir}", dirPath);
        }

        return hasMediaFiles;
    }

    private async Task<bool> ProcessFileAsync(string filePath, MediaBoxSettings config, CancellationToken ct)
    {
        if (FileNameParser.IsMediaFile(filePath) || FileNameParser.IsSubtitleFile(filePath))
        {
            await MoveMediaFileAsync(filePath, config, ct);
            return true;
        }

        MoveToUnknown(filePath, config);
        return false;
    }

    private async Task MoveMediaFileAsync(string filePath, MediaBoxSettings config, CancellationToken ct)
    {
        var fileName = Path.GetFileName(filePath);
        var parsed = FileNameParser.Parse(fileName);

        if (parsed.IsTvShow)
        {
            await MoveTvShowFileAsync(filePath, parsed, config, ct);
        }
        else
        {
            await MoveMovieFileAsync(filePath, parsed, config, ct);
        }
    }

    private async Task MoveTvShowFileAsync(string filePath, ParsedMediaInfo parsed, MediaBoxSettings config, CancellationToken ct)
    {
        var show = catalog.FindTvShow(parsed.CleanName, parsed.Year);
        string baseDir;

        if (show != null)
        {
            baseDir = show.FolderPath;
        }
        else
        {
            var year = parsed.Year;
            if (!year.HasValue)
            {
                logger.LogInformation("Looking up year for: {ShowName}", parsed.CleanName);
                year = await catalog.LookupTvShowYearAsync(parsed.CleanName, ct);

                if (year.HasValue)
                {
                    logger.LogInformation("Found year {Year} for: {ShowName}", year.Value, parsed.CleanName);
                }
                else
                {
                    logger.LogWarning("⚠️ Could not determine year for: {ShowName} - creating folder without year", parsed.CleanName);
                }
            }

            var folderName = FileNameParser.BuildFolderName(parsed.CleanName, year, catalog.GetPreferredYearFormat());

            var existingFolder = Directory.GetDirectories(config.TvShowsPath)
                .FirstOrDefault(d => string.Equals(Path.GetFileName(d), folderName, StringComparison.OrdinalIgnoreCase));

            if (existingFolder != null)
            {
                baseDir = existingFolder;
                logger.LogDebug("Using existing folder (case-insensitive match): {Folder}", Path.GetFileName(existingFolder));
            }
            else
            {
                baseDir = Path.Combine(config.TvShowsPath, folderName);
                Directory.CreateDirectory(baseDir);
                logger.LogInformation("Created new folder: {Folder}", folderName);
            }
        }

        var seasonDir = Path.Combine(baseDir, $"Season {parsed.Season!.Value:D2}");
        Directory.CreateDirectory(seasonDir);

        var destPath = Path.Combine(seasonDir, Path.GetFileName(filePath));
        if (File.Exists(destPath))
            destPath = GetUniquePath(destPath);

        File.Move(filePath, destPath);
        var msg = $"📺 Organized: {Path.GetFileName(filePath)} → {Path.GetFileName(baseDir)}/Season {parsed.Season.Value:D2}/";
        logger.LogInformation(msg);
        state.AddActivity(msg);
        await telegram.SendMessageAsync(msg, ct);
    }

    private async Task MoveMovieFileAsync(string filePath, ParsedMediaInfo parsed, MediaBoxSettings config, CancellationToken ct)
    {
        var movie = catalog.FindMovie(parsed.CleanName, parsed.Year);
        string destDir;

        if (movie != null)
        {
            destDir = movie.FolderPath;
        }
        else
        {
            var year = parsed.Year;
            if (!year.HasValue)
            {
                logger.LogInformation("Looking up year for: {MovieName}", parsed.CleanName);
                year = await catalog.LookupMovieYearAsync(parsed.CleanName, ct);

                if (year.HasValue)
                {
                    logger.LogInformation("Found year {Year} for: {MovieName}", year.Value, parsed.CleanName);
                }
                else
                {
                    logger.LogWarning("⚠️ Could not determine year for: {MovieName} - creating folder without year", parsed.CleanName);
                }
            }

            var folderName = FileNameParser.BuildFolderName(parsed.CleanName, year, catalog.GetPreferredYearFormat());

            var existingFolder = Directory.GetDirectories(config.MoviesPath)
                .FirstOrDefault(d => string.Equals(Path.GetFileName(d), folderName, StringComparison.OrdinalIgnoreCase));

            if (existingFolder != null)
            {
                destDir = existingFolder;
                logger.LogDebug("Using existing folder (case-insensitive match): {Folder}", Path.GetFileName(existingFolder));
            }
            else
            {
                destDir = Path.Combine(config.MoviesPath, folderName);
                Directory.CreateDirectory(destDir);
                logger.LogInformation("Created new folder: {Folder}", folderName);
            }
        }

        var destPath = Path.Combine(destDir, Path.GetFileName(filePath));
        if (File.Exists(destPath))
            destPath = GetUniquePath(destPath);

        File.Move(filePath, destPath);
        var msg = $"🎬 Organized: {Path.GetFileName(filePath)} → {Path.GetFileName(destDir)}/";
        logger.LogInformation(msg);
        state.AddActivity(msg);
        await telegram.SendMessageAsync(msg, ct);
    }

    private void MoveToUnknown(string filePath, MediaBoxSettings config)
    {
        try
        {
            Directory.CreateDirectory(config.UnknownPath);
            var destPath = Path.Combine(config.UnknownPath, Path.GetFileName(filePath));
            if (File.Exists(destPath))
                destPath = GetUniquePath(destPath);
            File.Move(filePath, destPath);
            logger.LogDebug("Moved to unknown: {File}", Path.GetFileName(filePath));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to move file to unknown: {File}", filePath);
        }
    }

    private static string GetUniquePath(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        var counter = 1;
        string newPath;
        do
        {
            newPath = Path.Combine(dir, $"{name} ({counter}){ext}");
            counter++;
        } while (File.Exists(newPath));
        return newPath;
    }
}
