using MediaBox2026.Models;
using Microsoft.Extensions.Options;

namespace MediaBox2026.Services;

public class MediaCatalogService(
    MediaDatabase db,
    IHttpClientFactory httpFactory,
    IOptionsMonitor<MediaBoxSettings> settings,
    MediaBoxState state,
    ITelegramNotifier telegram,
    ILogger<MediaCatalogService> logger)
{
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private FileNameParser.YearFormat _preferredYearFormat = FileNameParser.YearFormat.Parentheses;

    public async Task ScanAllAsync(CancellationToken ct = default)
    {
        if (!await _scanLock.WaitAsync(0, ct)) return;
        try
        {
            var startTime = DateTime.Now;
            logger.LogInformation("🚀 Starting full media scan at {Time}...", startTime.ToString("HH:mm:ss"));

            logger.LogInformation("Step 1/3: Scanning TV shows...");
            ScanTvShows();

            logger.LogInformation("Step 2/3: Scanning movies...");
            ScanMovies();

            logger.LogInformation("Step 3/3: Scanning YouTube videos...");
            ScanYouTube();

            state.LastMediaScan = DateTime.Now;
            state.TvShowCount = db.TvShows.Count();
            state.MovieCount = db.Movies.Count();
            state.YouTubeCount = db.YouTubeVideos.Count();
            state.WatchlistCount = db.Watchlist.Count(w => w.Status == WatchlistStatus.Pending);

            var totalEpisodes = db.TvShows.FindAll().Sum(s => s.Episodes.Count);

            state.NotifyChange();

            var scanDuration = (DateTime.Now - startTime).TotalSeconds;
            logger.LogInformation("✅ Media scan complete in {Duration:F1}s. TV Shows: {Tv} ({Episodes} episodes), Movies: {Mov}, YouTube: {Yt}",
                scanDuration, state.TvShowCount, totalEpisodes, state.MovieCount, state.YouTubeCount);

            logger.LogInformation("🔍 Starting duplicate detection...");
            await DetectAndNotifyDuplicateFolders(ct);

            logger.LogInformation("📅 Starting year fixing process...");
            await FixFoldersWithoutYears(ct);

            var totalDuration = (DateTime.Now - startTime).TotalSeconds;
            logger.LogInformation("🎉 All scan operations complete in {Duration:F1}s", totalDuration);
        }
        finally
        {
            _scanLock.Release();
        }
    }

    private async Task FixFoldersWithoutYears(CancellationToken ct)
    {
        try
        {
            var tvShowsPath = settings.CurrentValue.TvShowsPath;
            if (!Directory.Exists(tvShowsPath)) return;

            DetectPreferredYearFormat(tvShowsPath);

            var allShows = db.TvShows.FindAll().ToList();
            var showsWithYears = allShows.Where(s => s.Year.HasValue).ToList();
            var showsWithoutYears = allShows.Where(s => !s.Year.HasValue).ToList();

            logger.LogInformation("📊 Library status: {WithYears} shows have years, {WithoutYears} shows need year lookup", 
                showsWithYears.Count, showsWithoutYears.Count);

            if (showsWithoutYears.Count == 0)
            {
                logger.LogInformation("✅ All TV show folders already have years");
                await StandardizeYearFormats(tvShowsPath, ct);
                return;
            }

            logger.LogInformation("🔍 Looking up years for {Count} shows via TVMaze API (shows without year in folder name)...", 
                showsWithoutYears.Count);

            var processed = 0;
            var successful = 0;

            foreach (var show in showsWithoutYears)
            {
                try
                {
                    processed++;
                    var startTime = DateTime.Now;

                    logger.LogInformation("🌐 API lookup [{Processed}/{Total}]: {Show}", 
                        processed, showsWithoutYears.Count, show.Name);

                    var year = await LookupTvShowYearAsync(show.Name, ct);

                    var apiDuration = (DateTime.Now - startTime).TotalMilliseconds;

                    if (!year.HasValue)
                    {
                        logger.LogWarning("⚠️ No year found ({Duration:F0}ms): {Show}", apiDuration, show.Name);
                        continue;
                    }

                    logger.LogInformation("✅ Found year {Year} ({Duration:F0}ms): {Show}", year.Value, apiDuration, show.Name);

                    var currentFolder = show.FolderPath;
                    var folderName = Path.GetFileName(currentFolder);
                    var newFolderName = FileNameParser.BuildFolderName(show.Name, year, _preferredYearFormat);
                    var newFolderPath = Path.Combine(tvShowsPath, newFolderName);

                    if (Directory.Exists(newFolderPath))
                    {
                        logger.LogWarning("Target folder already exists, skipping: {Show} -> {NewFolder}", show.Name, newFolderName);
                        continue;
                    }

                    Directory.Move(currentFolder, newFolderPath);
                    show.Year = year;
                    show.FolderPath = newFolderPath;
                    db.TvShows.Update(show);
                    successful++;

                    logger.LogInformation("📁 [{Successful}/{Total}] Renamed: {Old} -> {New}", 
                        successful, showsWithoutYears.Count, folderName, newFolderName);
                    await telegram.SendMessageAsync($"📁 Added year to folder: `{folderName}` → `{newFolderName}`", ct);

                    if (processed < showsWithoutYears.Count)
                    {
                        await Task.Delay(250, ct);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to fix folder for: {Show}", show.Name);
                }
            }

            logger.LogInformation("✅ Year lookup complete: {Successful} successful, {Failed} failed out of {Total}", 
                successful, showsWithoutYears.Count - successful, showsWithoutYears.Count);

            await StandardizeYearFormats(tvShowsPath, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fixing folders without years");
        }
    }

    private void DetectPreferredYearFormat(string tvShowsPath)
    {
        var folders = Directory.GetDirectories(tvShowsPath);
        var formatCounts = new Dictionary<FileNameParser.YearFormat, int>
        {
            [FileNameParser.YearFormat.Parentheses] = 0,
            [FileNameParser.YearFormat.Brackets] = 0,
            [FileNameParser.YearFormat.Space] = 0
        };

        foreach (var folder in folders)
        {
            var folderName = Path.GetFileName(folder);

            if (System.Text.RegularExpressions.Regex.IsMatch(folderName, @"\((?:19|20)\d{2}\)"))
                formatCounts[FileNameParser.YearFormat.Parentheses]++;
            else if (System.Text.RegularExpressions.Regex.IsMatch(folderName, @"\[(?:19|20)\d{2}\]"))
                formatCounts[FileNameParser.YearFormat.Brackets]++;
            else if (System.Text.RegularExpressions.Regex.IsMatch(folderName, @"\s(?:19|20)\d{2}$"))
                formatCounts[FileNameParser.YearFormat.Space]++;
        }

        // Always use parentheses format for consistency
        _preferredYearFormat = FileNameParser.YearFormat.Parentheses;

        logger.LogInformation("Using standard year format: Name (Year) - Current library has {Parentheses} parentheses, {Brackets} brackets, {Space} space format folders",
            formatCounts[FileNameParser.YearFormat.Parentheses], 
            formatCounts[FileNameParser.YearFormat.Brackets], 
            formatCounts[FileNameParser.YearFormat.Space]);
    }

    private async Task StandardizeYearFormats(string tvShowsPath, CancellationToken ct)
    {
        try
        {
            logger.LogInformation("📐 Checking for inconsistent year formats...");

            var shows = db.TvShows.FindAll().Where(s => s.Year.HasValue).ToList();
            var needsStandardization = shows
                .Where(s => Path.GetFileName(s.FolderPath) != FileNameParser.BuildFolderName(s.Name, s.Year, _preferredYearFormat))
                .ToList();

            if (needsStandardization.Count == 0)
            {
                logger.LogInformation("✅ All folders already use consistent year format");
                return;
            }

            logger.LogInformation("Found {Count} folders with inconsistent year format, standardizing...", needsStandardization.Count);

            var renamed = 0;
            var processed = 0;

            foreach (var show in needsStandardization)
            {
                processed++;
                var currentFolder = show.FolderPath;
                var currentFolderName = Path.GetFileName(currentFolder);
                var expectedFolderName = FileNameParser.BuildFolderName(show.Name, show.Year, _preferredYearFormat);

                var (parsedName, parsedYear) = FileNameParser.ParseFolderName(currentFolderName);
                if (!parsedYear.HasValue || parsedYear != show.Year)
                {
                    logger.LogDebug("Skipping {Folder} - year mismatch or not parseable", currentFolderName);
                    continue;
                }

                var newFolderPath = Path.Combine(tvShowsPath, expectedFolderName);
                if (Directory.Exists(newFolderPath))
                {
                    logger.LogWarning("Cannot standardize format, target folder exists: {Current} -> {Expected}", 
                        currentFolderName, expectedFolderName);
                    continue;
                }

                try
                {
                    logger.LogInformation("Standardizing format ({Processed}/{Total}): {Old} -> {New}", 
                        processed, needsStandardization.Count, currentFolderName, expectedFolderName);

                    Directory.Move(currentFolder, newFolderPath);
                    show.FolderPath = newFolderPath;
                    db.TvShows.Update(show);
                    renamed++;

                    logger.LogInformation("✅ [{Renamed}/{Total}] Standardized: {FolderName}", 
                        renamed, needsStandardization.Count, expectedFolderName);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to standardize folder: {Folder}", currentFolderName);
                }
            }

            if (renamed > 0)
            {
                logger.LogInformation("✅ Standardized {Count} folder name(s) to consistent year format", renamed);
                await telegram.SendMessageAsync($"📐 Standardized {renamed} folder(s) to use consistent year format", ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error standardizing year formats");
        }
    }

    private void ScanTvShows()
    {
        var path = settings.CurrentValue.TvShowsPath;
        if (!Directory.Exists(path)) return;

        var directories = Directory.GetDirectories(path);
        var totalDirs = directories.Length;
        var processed = 0;

        logger.LogInformation("📺 Scanning {Count} TV show folders...", totalDirs);

        foreach (var dir in directories)
        {
            try
            {
                processed++;
                var folderName = Path.GetFileName(dir);

                if (processed % 10 == 0 || processed == totalDirs)
                {
                    logger.LogInformation("Progress: {Processed}/{Total} folders scanned ({Percent}%)", 
                        processed, totalDirs, (processed * 100 / totalDirs));
                }

                var (name, year) = FileNameParser.ParseFolderName(folderName);

                var show = db.TvShows.FindOne(s => s.FolderPath == dir);
                if (show != null && show.Id == 0)
                {
                    db.TvShows.DeleteMany(s => s.FolderPath == dir);
                    show = null;
                }
                var isNew = show == null;
                show ??= new TvShow { FolderPath = dir, Episodes = [] };

                show.Name = name;
                show.Year = year;
                show.LastScanned = DateTime.UtcNow;

                show.Episodes.Clear();

                logger.LogDebug("Scanning files in: {Folder}", folderName);
                var files = Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories)
                    .Where(f => FileNameParser.IsMediaFile(f) || FileNameParser.IsSubtitleFile(f));

                var fileCount = 0;
                foreach (var file in files)
                {
                    fileCount++;
                    var parsed = FileNameParser.Parse(Path.GetFileName(file));
                    if (parsed.Season.HasValue && parsed.Episode.HasValue)
                    {
                        show.Episodes.Add(new EpisodeInfo
                        {
                            Season = parsed.Season.Value,
                            Episode = parsed.Episode.Value,
                            FileName = Path.GetFileName(file)
                        });
                    }
                }

                if (fileCount > 100)
                {
                    logger.LogDebug("Scanned {Count} files in: {Folder}", fileCount, folderName);
                }

                if (show.Episodes.Count > 0)
                {
                    show.LatestSeason = show.Episodes.Max(e => e.Season);
                    show.LatestEpisode = show.Episodes
                        .Where(e => e.Season == show.LatestSeason)
                        .Max(e => e.Episode);
                    logger.LogDebug("Scanned {Show}: {EpisodeCount} episodes (Latest: S{Season}E{Episode})",
                        show.Name, show.Episodes.Count, show.LatestSeason, show.LatestEpisode);
                }
                else
                {
                    logger.LogDebug("Scanned {Show}: No episodes found", show.Name);
                }

                if (isNew)
                    db.TvShows.Insert(show);
                else
                    db.TvShows.Update(show);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error scanning TV show folder: {Dir}", dir);
            }
        }

        logger.LogInformation("✅ Completed scanning {Total} TV show folders", totalDirs);
    }

    private void ScanMovies()
    {
        var path = settings.CurrentValue.MoviesPath;
        if (!Directory.Exists(path)) return;

        foreach (var dir in Directory.GetDirectories(path))
        {
            try
            {
                var folderName = Path.GetFileName(dir);
                var (name, year) = FileNameParser.ParseFolderName(folderName);

                var mediaFile = Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories)
                    .FirstOrDefault(f => FileNameParser.IsMediaFile(f));

                if (mediaFile == null) continue;

                var movie = db.Movies.FindOne(m => m.FolderPath == dir);
                if (movie != null && movie.Id == 0)
                {
                    db.Movies.DeleteMany(m => m.FolderPath == dir);
                    movie = null;
                }
                if (movie == null)
                {
                    db.Movies.Insert(new Movie
                    {
                        Name = name,
                        Year = year,
                        FolderPath = dir,
                        FileName = Path.GetFileName(mediaFile),
                        LastScanned = DateTime.UtcNow
                    });
                }
                else
                {
                    movie.Name = name;
                    movie.Year = year;
                    movie.FileName = Path.GetFileName(mediaFile);
                    movie.LastScanned = DateTime.UtcNow;
                    db.Movies.Update(movie);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error scanning movie folder: {Dir}", dir);
            }
        }
    }

    private void ScanYouTube()
    {
        var path = settings.CurrentValue.YouTubePath;
        if (!Directory.Exists(path)) return;

        foreach (var uploaderDir in Directory.GetDirectories(path))
        {
            var uploader = Path.GetFileName(uploaderDir);
            foreach (var file in Directory.GetFiles(uploaderDir, "*.*", SearchOption.AllDirectories))
            {
                try
                {
                    if (!FileNameParser.IsMediaFile(file)) continue;

                    var fileName = Path.GetFileName(file);
                    var existing = db.YouTubeVideos.FindOne(v => v.FilePath == file);
                    if (existing != null) continue;

                    db.YouTubeVideos.Insert(new YouTubeVideo
                    {
                        Uploader = uploader,
                        Title = Path.GetFileNameWithoutExtension(fileName),
                        FileName = fileName,
                        FilePath = file,
                        CatalogedDate = DateTime.UtcNow
                    });
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error scanning YouTube file: {File}", file);
                }
            }
        }
    }

    public bool HasEpisode(string showName, int season, int episode, int? year = null)
    {
        var shows = db.TvShows.FindAll().ToList();

        TvShow? match = null;
        if (year.HasValue)
        {
            match = shows.FirstOrDefault(s => 
                FileNameParser.FuzzyMatch(s.Name, showName) >= 0.5 && 
                s.Year == year);

            if (match == null)
            {
                match = shows.FirstOrDefault(s => 
                    FileNameParser.FuzzyMatch(s.Name, showName) >= 0.5 && 
                    !s.Year.HasValue);
            }
        }

        if (match == null)
        {
            match = shows
                .Where(s => FileNameParser.FuzzyMatch(s.Name, showName) >= 0.5)
                .OrderByDescending(s => s.Year.HasValue ? 1 : 0)
                .ThenByDescending(s => s.LatestSeason)
                .FirstOrDefault();
        }

        var hasIt = match?.Episodes.Any(e => e.Season == season && e.Episode == episode) ?? false;
        logger.LogInformation("HasEpisode check: {ShowName} S{Season}E{Episode} (year: {Year}) - Matched folder: '{Match}' (year: {MatchYear}, score: {Score}), HasIt: {HasIt}", 
            showName, season, episode, year, match?.Name ?? "none", match?.Year,
            match != null ? FileNameParser.FuzzyMatch(match.Name, showName) : 0, hasIt);
        return hasIt;
    }

    public TvShow? FindTvShow(string name, int? year = null)
    {
        var shows = db.TvShows.FindAll().ToList();

        var candidates = shows
            .Select(s => (Show: s, Score: FileNameParser.FuzzyMatch(s.Name, name)))
            .Where(x => x.Score >= 0.5)
            .OrderByDescending(x => x.Score)
            .ToList();

        TvShow? result = null;
        if (year.HasValue)
        {
            result = candidates.FirstOrDefault(x => x.Show.Year == year).Show;

            if (result == null)
            {
                result = candidates.FirstOrDefault(x => !x.Show.Year.HasValue).Show;
            }
        }

        result ??= candidates.FirstOrDefault().Show;

        logger.LogInformation("FindTvShow: '{Name}' (year: {Year}) -> Matched folder: '{Result}' (year: {ResultYear}, score: {Score})", 
            name, year, result?.Name ?? "none", result?.Year,
            result != null ? FileNameParser.FuzzyMatch(result.Name, name) : 0);
        return result;
    }

    public Movie? FindMovie(string name, int? year = null)
    {
        var movies = db.Movies.FindAll().ToList();
        return movies
            .Select(m => (Movie: m, Score: FileNameParser.FuzzyMatch(m.Name, name)))
            .Where(x => x.Score > 0.6 && (!year.HasValue || x.Movie.Year == year))
            .OrderByDescending(x => x.Score)
            .FirstOrDefault().Movie;
    }

    public int GetLatestSeason(string showName)
    {
        var show = FindTvShow(showName);
        return show?.LatestSeason ?? 0;
    }

    public FileNameParser.YearFormat GetPreferredYearFormat() => _preferredYearFormat;

    public async Task<int?> LookupTvShowYearAsync(string name, CancellationToken ct = default)
    {
        try
        {
            using var http = httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(10);

            var url = $"https://api.tvmaze.com/singlesearch/shows?q={Uri.EscapeDataString(name)}";
            logger.LogDebug("API call: {Url}", url);

            var response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug("API returned {Status} for: {Name}", response.StatusCode, name);
                return null;
            }

            var jsonOptions = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
            };
            var json = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(jsonOptions, ct);
            if (json.TryGetProperty("premiered", out var premiered))
            {
                var dateStr = premiered.GetString();
                if (DateTime.TryParse(dateStr, out var date))
                {
                    logger.LogDebug("Found year {Year} for: {Name}", date.Year, name);
                    return date.Year;
                }
            }
        }
        catch (TaskCanceledException ex)
        {
            logger.LogWarning("API timeout for: {Name} - {Message}", name, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("API request failed for: {Name} - {Message}", name, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to lookup TV show year for: {Name}", name);
        }
        return null;
    }

    public async Task<int?> LookupMovieYearAsync(string name, CancellationToken ct = default)
    {
        try
        {
            using var http = httpFactory.CreateClient();
            var url = $"https://yts.bz/api/v2/list_movies.json?query_term={Uri.EscapeDataString(name)}&limit=1";
            var response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(ct);
            if (json.TryGetProperty("data", out var data) &&
                data.TryGetProperty("movies", out var movies) &&
                movies.GetArrayLength() > 0)
            {
                return movies[0].GetProperty("year").GetInt32();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to lookup movie year for: {Name}", name);
        }
        return null;
    }

    private async Task DetectAndNotifyDuplicateFolders(CancellationToken ct)
    {
        try
        {
            logger.LogInformation("🔍 Checking for duplicate show folders...");

            var shows = db.TvShows.FindAll().ToList();
            var duplicateGroups = shows
                .GroupBy(s => s.Name.ToLowerInvariant())
                .Where(g => g.Count() > 1)
                .ToList();

            if (duplicateGroups.Count == 0)
            {
                logger.LogInformation("✅ No duplicate show folders detected");
                return;
            }

            logger.LogInformation("⚠️ Found {Count} show(s) with duplicate folders", duplicateGroups.Count);

            foreach (var group in duplicateGroups)
            {
                var showList = group.ToList();
                var folderNames = string.Join(", ", showList.Select(s => $"{s.Name}{(s.Year.HasValue ? $" ({s.Year})" : "")}"));
                var showKey = group.Key;

                var alreadyNotified = db.NotifiedDuplicates.Exists(n => n.ShowName == showKey);
                if (alreadyNotified)
                {
                    logger.LogDebug("Duplicate folders already notified for: {Show}", showKey);
                    continue;
                }

                logger.LogWarning("Duplicate folders detected for show: {Show} - Folders: {Folders}", showKey, folderNames);

                var message = $"⚠️ *Duplicate Folders Detected*\n\n" +
                             $"Show: `{group.First().Name}`\n" +
                             $"Folders:\n{string.Join("\n", showList.Select(s => $"  • {Path.GetFileName(s.FolderPath)} ({s.Episodes.Count} episodes)"))}\n\n" +
                             $"Consider consolidating these folders to avoid duplicate downloads.";

                await telegram.SendMessageAsync(message, ct);

                db.NotifiedDuplicates.Insert(new NotifiedDuplicate
                {
                    ShowName = showKey,
                    FolderNames = folderNames,
                    NotifiedDate = DateTime.UtcNow
                });

                logger.LogInformation("Telegram notification sent for duplicate folders: {Show}", showKey);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error detecting duplicate folders");
        }
    }
}
