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
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(45), ct);
        logger.LogInformation("Download organizer started");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await OrganizeAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Download organizer error");
            }

            await Task.Delay(TimeSpan.FromMinutes(settings.CurrentValue.DownloadOrganizerMinutes), ct);
        }
    }

    private async Task OrganizeAsync(CancellationToken ct)
    {
        var config = settings.CurrentValue;
        if (!Directory.Exists(config.DownloadsPath)) return;

        var activeTorrents = await transmission.GetTorrentsAsync(ct);
        var activeNames = activeTorrents
            .Where(t => !t.IsFinished)
            .Select(t => t.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var movedAny = false;
        var entries = Directory.GetFileSystemEntries(config.DownloadsPath);
        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            if (activeNames.Contains(name)) continue;

            if (Directory.Exists(entry))
                movedAny |= await ProcessDirectoryAsync(entry, config, ct);
            else
                movedAny |= await ProcessFileAsync(entry, config, ct);
        }

        if (movedAny)
            await jellyfin.TriggerLibraryScanAsync(ct);
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
        var show = catalog.FindTvShow(parsed.CleanName);
        string baseDir;

        if (show != null)
        {
            baseDir = show.FolderPath;
        }
        else
        {
            var year = parsed.Year;
            if (!year.HasValue)
                year = await catalog.LookupTvShowYearAsync(parsed.CleanName, ct);

            var folderName = FileNameParser.BuildFolderName(parsed.CleanName, year);
            baseDir = Path.Combine(config.TvShowsPath, folderName);
            Directory.CreateDirectory(baseDir);
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
                year = await catalog.LookupMovieYearAsync(parsed.CleanName, ct);

            var folderName = FileNameParser.BuildFolderName(parsed.CleanName, year);
            destDir = Path.Combine(config.MoviesPath, folderName);
            Directory.CreateDirectory(destDir);
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
