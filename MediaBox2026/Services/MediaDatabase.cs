using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using MediaBox2026.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace MediaBox2026.Services;

public class MediaDatabase : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly ILogger<MediaDatabase> _logger;
    internal readonly object DbLock = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public DbCollection<TvShow> TvShows { get; }
    public DbCollection<Movie> Movies { get; }
    public DbCollection<YouTubeVideo> YouTubeVideos { get; }
    public DbCollection<WatchlistItem> Watchlist { get; }
    public DbCollection<PendingDownload> PendingDownloads { get; }
    public DbCollection<ProcessedRssItem> ProcessedRssItems { get; }
    public DbCollection<DispatchedEpisode> DispatchedEpisodes { get; }
    public DbCollection<PendingLargeTorrent> PendingLargeTorrents { get; }
    public DbCollection<RssFeedSubscription> RssFeedSubscriptions { get; }
    public DbCollection<ProcessedFeedItem> ProcessedFeedItems { get; }
    public DbCollection<NotifiedDuplicate> NotifiedDuplicates { get; }

    public MediaDatabase(IOptions<MediaBoxSettings> settings, ILogger<MediaDatabase> logger)
    {
        _logger = logger;
        var dbPath = settings.Value.DatabasePath;
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
            logger.LogInformation("Database directory ensured: {Directory}", dir);
        }

        MigrateFromLiteDb(dbPath);

        try
        {
            _db = new SqliteConnection($"Data Source={dbPath}");
            _db.Open();
            logger.LogInformation("✅ SQLite database connection opened: {Path}", dbPath);

            // Enable WAL mode for better concurrency
            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = "PRAGMA journal_mode=WAL";
                var mode = cmd.ExecuteScalar()?.ToString();
                logger.LogInformation("Database journal mode: {Mode}", mode);
            }

            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = "PRAGMA synchronous=NORMAL";
                cmd.ExecuteNonQuery();
            }

            // Verify database integrity
            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = "PRAGMA integrity_check";
                var integrity = cmd.ExecuteScalar()?.ToString();
                if (integrity != "ok")
                {
                    logger.LogWarning("⚠️ Database integrity check: {Result}", integrity);
                }
                else
                {
                    logger.LogInformation("✅ Database integrity verified");
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "❌ Failed to initialize database at {Path}", dbPath);
            throw;
        }

        TvShows = new DbCollection<TvShow>(_db, DbLock, "tvshows", JsonOpts);
        Movies = new DbCollection<Movie>(_db, DbLock, "movies", JsonOpts);
        YouTubeVideos = new DbCollection<YouTubeVideo>(_db, DbLock, "youtube", JsonOpts);
        Watchlist = new DbCollection<WatchlistItem>(_db, DbLock, "watchlist", JsonOpts);
        PendingDownloads = new DbCollection<PendingDownload>(_db, DbLock, "pending", JsonOpts);
        ProcessedRssItems = new DbCollection<ProcessedRssItem>(_db, DbLock, "rss_processed", JsonOpts);
        DispatchedEpisodes = new DbCollection<DispatchedEpisode>(_db, DbLock, "dispatched", JsonOpts);
        PendingLargeTorrents = new DbCollection<PendingLargeTorrent>(_db, DbLock, "pending_large_torrents", JsonOpts);
        RssFeedSubscriptions = new DbCollection<RssFeedSubscription>(_db, DbLock, "rss_feed_subscriptions", JsonOpts);
        ProcessedFeedItems = new DbCollection<ProcessedFeedItem>(_db, DbLock, "processed_feed_items", JsonOpts);
        NotifiedDuplicates = new DbCollection<NotifiedDuplicate>(_db, DbLock, "notified_duplicates", JsonOpts);

        _logger.LogInformation("📊 Database collections initialized:");
        _logger.LogInformation("  - TV Shows: {Count}", TvShows.Count());
        _logger.LogInformation("  - Movies: {Count}", Movies.Count());
        _logger.LogInformation("  - Pending Downloads: {Count}", PendingDownloads.Count());
        _logger.LogInformation("  - Watchlist: {Count}", Watchlist.Count());
        _logger.LogInformation("✅ SQLite database fully initialized at {Path}", dbPath);
    }

    private void MigrateFromLiteDb(string dbPath)
    {
        if (!File.Exists(dbPath)) return;

        try
        {
            var header = new byte[16];
            using var fs = File.OpenRead(dbPath);
            if (fs.Read(header, 0, 16) >= 16)
            {
                // SQLite files always start with this exact 16-byte header string
                var sqliteMagic = "SQLite format 3\0"u8;
                if (!header.AsSpan(0, 16).SequenceEqual(sqliteMagic))
                {
                    fs.Close();
                    File.Delete(dbPath);
                    // Clean up any LiteDB journal/log files too
                    foreach (var f in Directory.GetFiles(Path.GetDirectoryName(dbPath)!,
                        Path.GetFileName(dbPath) + "*"))
                        File.Delete(f);
                    _logger.LogWarning("Deleted old LiteDB database. Scan data will be rebuilt automatically.");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not check database file format during migration.");
        }
    }

    public void Dispose() => _db.Dispose();
}

/// <summary>
/// Lightweight document collection backed by SQLite.
/// Each row stores (id INTEGER PK, data TEXT as JSON).
/// Predicates are evaluated in-memory — fine for datasets under a few thousand items.
/// </summary>
public class DbCollection<T> where T : class, IEntity, new()
{
    private readonly SqliteConnection _db;
    private readonly object _lock;
    private readonly string _table;
    private readonly JsonSerializerOptions _jsonOpts;

    internal DbCollection(SqliteConnection db, object lk, string table, JsonSerializerOptions jsonOpts)
    {
        _db = db;
        _lock = lk;
        _table = table;
        _jsonOpts = jsonOpts;

        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"CREATE TABLE IF NOT EXISTS [{_table}] (id INTEGER PRIMARY KEY AUTOINCREMENT, data TEXT NOT NULL)";
            cmd.ExecuteNonQuery();
        }
    }

    public T? FindOne(Func<T, bool> predicate) => FindAll().FirstOrDefault(predicate);

    public IEnumerable<T> Find(Func<T, bool> predicate) => FindAll().Where(predicate).ToList();

    public List<T> FindAll()
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"SELECT id, data FROM [{_table}]";
            using var reader = cmd.ExecuteReader();
            var results = new List<T>();
            while (reader.Read())
            {
                var id = reader.GetInt32(0);
                var json = reader.GetString(1);
                var entity = JsonSerializer.Deserialize<T>(json, _jsonOpts)!;
                entity.Id = id;
                results.Add(entity);
            }
            return results;
        }
    }

    public void Insert(T entity)
    {
        lock (_lock)
        {
            entity.Id = 0;
            var json = JsonSerializer.Serialize(entity, _jsonOpts);
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"INSERT INTO [{_table}] (data) VALUES (@data) RETURNING id";
            cmd.Parameters.AddWithValue("@data", json);
            var id = Convert.ToInt32(cmd.ExecuteScalar());
            entity.Id = id;
        }
    }

    public bool Update(T entity)
    {
        lock (_lock)
        {
            var id = entity.Id;
            var json = JsonSerializer.Serialize(entity, _jsonOpts);
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"UPDATE [{_table}] SET data = @data WHERE id = @id";
            cmd.Parameters.AddWithValue("@data", json);
            cmd.Parameters.AddWithValue("@id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public int DeleteMany(Func<T, bool> predicate)
    {
        lock (_lock)
        {
            var ids = ReadIdsAndEntities().Where(x => predicate(x.Entity)).Select(x => x.Id).ToList();
            if (ids.Count == 0) return 0;

            using var cmd = _db.CreateCommand();
            var paramNames = new List<string>();
            for (var i = 0; i < ids.Count; i++)
            {
                var p = $"@id{i}";
                paramNames.Add(p);
                cmd.Parameters.AddWithValue(p, ids[i]);
            }
            cmd.CommandText = $"DELETE FROM [{_table}] WHERE id IN ({string.Join(",", paramNames)})";
            return cmd.ExecuteNonQuery();
        }
    }

    public int Count()
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM [{_table}]";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public int Count(Func<T, bool> predicate) => FindAll().Count(predicate);

    public bool Exists(Func<T, bool> predicate) => FindAll().Any(predicate);

    // No-op — in-memory filtering doesn't need indexes
    public bool EnsureIndex<K>(Expression<Func<T, K>> keySelector) => true;

    private List<(int Id, T Entity)> ReadIdsAndEntities()
    {
        // Must be called within lock
        using var cmd = _db.CreateCommand();
        cmd.CommandText = $"SELECT id, data FROM [{_table}]";
        using var reader = cmd.ExecuteReader();
        var results = new List<(int, T)>();
        while (reader.Read())
        {
            var id = reader.GetInt32(0);
            var json = reader.GetString(1);
            var entity = JsonSerializer.Deserialize<T>(json, _jsonOpts)!;
            entity.Id = id;
            results.Add((id, entity));
        }
        return results;
    }
}
