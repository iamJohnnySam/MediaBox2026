using LiteDB;
using MediaBox2026.Models;
using Microsoft.Extensions.Options;

namespace MediaBox2026.Services;

public class MediaDatabase : IDisposable
{
    private readonly ILiteDatabase _db;
    private readonly ILogger<MediaDatabase> _logger;

    public ILiteCollection<TvShow> TvShows => _db.GetCollection<TvShow>("tvshows");
    public ILiteCollection<Movie> Movies => _db.GetCollection<Movie>("movies");
    public ILiteCollection<YouTubeVideo> YouTubeVideos => _db.GetCollection<YouTubeVideo>("youtube");
    public ILiteCollection<WatchlistItem> Watchlist => _db.GetCollection<WatchlistItem>("watchlist");
    public ILiteCollection<PendingDownload> PendingDownloads => _db.GetCollection<PendingDownload>("pending");
    public ILiteCollection<ProcessedRssItem> ProcessedRssItems => _db.GetCollection<ProcessedRssItem>("rss_processed");
    public ILiteCollection<DispatchedEpisode> DispatchedEpisodes => _db.GetCollection<DispatchedEpisode>("dispatched");

    public MediaDatabase(IOptions<MediaBoxSettings> settings, ILogger<MediaDatabase> logger)
    {
        _logger = logger;
        var dbPath = settings.Value.DatabasePath;
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _db = new LiteDatabase($"Filename={dbPath};Connection=Direct");

        // Repair scan-based collections that may have corrupted _id types from previous bugs.
        // These collections are fully rebuilt from the filesystem on every media scan.
        var repaired = RepairCollectionIfCorrupted("tvshows")
                     | RepairCollectionIfCorrupted("movies")
                     | RepairCollectionIfCorrupted("youtube");

        if (repaired)
        {
            _db.Rebuild();
            _logger.LogWarning("Database rebuilt to clean up residual corruption.");
        }

        TvShows.EnsureIndex(x => x.Name);
        TvShows.EnsureIndex(x => x.FolderPath);
        Movies.EnsureIndex(x => x.Name);
        Movies.EnsureIndex(x => x.FolderPath);
        Watchlist.EnsureIndex(x => x.Status);
        PendingDownloads.EnsureIndex(x => x.Status);
        ProcessedRssItems.EnsureIndex(x => x.Guid);
        DispatchedEpisodes.EnsureIndex(x => x.ShowName);
    }

    private bool RepairCollectionIfCorrupted(string name)
    {
        if (!_db.CollectionExists(name)) return false;

        try
        {
            var col = _db.GetCollection(name);
            var hasCorruption = col.FindAll().Any(d => !d["_id"].IsInt32);
            if (hasCorruption)
            {
                _db.DropCollection(name);
                _logger.LogWarning("Dropped corrupted collection '{Name}' (mixed _id types). Data will be re-scanned.", name);
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            _db.DropCollection(name);
            _logger.LogWarning(ex, "Dropped unreadable collection '{Name}'. Data will be re-scanned.", name);
            return true;
        }
    }

    public void Dispose() => _db.Dispose();
}
