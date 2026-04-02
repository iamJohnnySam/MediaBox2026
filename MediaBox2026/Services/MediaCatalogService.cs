using MediaBox2026.Models;
using Microsoft.Extensions.Options;

namespace MediaBox2026.Services;

public class MediaCatalogService(
    MediaDatabase db,
    IHttpClientFactory httpFactory,
    IOptionsMonitor<MediaBoxSettings> settings,
    MediaBoxState state,
    ILogger<MediaCatalogService> logger)
{
    private readonly SemaphoreSlim _scanLock = new(1, 1);

    public async Task ScanAllAsync(CancellationToken ct = default)
    {
        if (!await _scanLock.WaitAsync(0, ct)) return;
        try
        {
            logger.LogInformation("Starting full media scan...");
            ScanTvShows();
            ScanMovies();
            ScanYouTube();
            state.LastMediaScan = DateTime.Now;
            state.TvShowCount = db.TvShows.Count();
            state.MovieCount = db.Movies.Count();
            state.YouTubeCount = db.YouTubeVideos.Count();
            state.WatchlistCount = db.Watchlist.Count(w => w.Status == WatchlistStatus.Pending);
            state.NotifyChange();
            logger.LogInformation("Media scan complete. TV: {Tv}, Movies: {Mov}, YouTube: {Yt}",
                state.TvShowCount, state.MovieCount, state.YouTubeCount);
        }
        finally
        {
            _scanLock.Release();
        }
    }

    private void ScanTvShows()
    {
        var path = settings.CurrentValue.TvShowsPath;
        if (!Directory.Exists(path)) return;

        foreach (var dir in Directory.GetDirectories(path))
        {
            try
            {
                var folderName = Path.GetFileName(dir);
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
                var files = Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories)
                    .Where(f => FileNameParser.IsMediaFile(f) || FileNameParser.IsSubtitleFile(f));

                foreach (var file in files)
                {
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

                if (show.Episodes.Count > 0)
                {
                    show.LatestSeason = show.Episodes.Max(e => e.Season);
                    show.LatestEpisode = show.Episodes
                        .Where(e => e.Season == show.LatestSeason)
                        .Max(e => e.Episode);
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

    public bool HasEpisode(string showName, int season, int episode)
    {
        var shows = db.TvShows.FindAll().ToList();
        var match = shows.FirstOrDefault(s => FileNameParser.FuzzyMatch(s.Name, showName) > 0.7);
        return match?.Episodes.Any(e => e.Season == season && e.Episode == episode) ?? false;
    }

    public TvShow? FindTvShow(string name)
    {
        var shows = db.TvShows.FindAll().ToList();
        return shows
            .Select(s => (Show: s, Score: FileNameParser.FuzzyMatch(s.Name, name)))
            .Where(x => x.Score > 0.6)
            .OrderByDescending(x => x.Score)
            .FirstOrDefault().Show;
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

    public async Task<int?> LookupTvShowYearAsync(string name, CancellationToken ct = default)
    {
        try
        {
            using var http = httpFactory.CreateClient();
            var url = $"https://api.tvmaze.com/singlesearch/shows?q={Uri.EscapeDataString(name)}";
            var response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(ct);
            if (json.TryGetProperty("premiered", out var premiered))
            {
                var dateStr = premiered.GetString();
                if (DateTime.TryParse(dateStr, out var date))
                    return date.Year;
            }
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
}
