using LiteDB;
using MediaBox2026.Models;
using Microsoft.Extensions.Options;

namespace MediaBox2026.Services;

public class MediaDatabase : IDisposable
{
    private readonly ILiteDatabase _db;

    public ILiteCollection<TvShow> TvShows => _db.GetCollection<TvShow>("tvshows");
    public ILiteCollection<Movie> Movies => _db.GetCollection<Movie>("movies");
    public ILiteCollection<YouTubeVideo> YouTubeVideos => _db.GetCollection<YouTubeVideo>("youtube");
    public ILiteCollection<WatchlistItem> Watchlist => _db.GetCollection<WatchlistItem>("watchlist");
    public ILiteCollection<PendingDownload> PendingDownloads => _db.GetCollection<PendingDownload>("pending");
    public ILiteCollection<ProcessedRssItem> ProcessedRssItems => _db.GetCollection<ProcessedRssItem>("rss_processed");
    public ILiteCollection<TelegramSession> TelegramSessions => _db.GetCollection<TelegramSession>("telegram");
    public ILiteCollection<DispatchedEpisode> DispatchedEpisodes => _db.GetCollection<DispatchedEpisode>("dispatched");

    public MediaDatabase(IOptions<MediaBoxSettings> settings)
    {
        var dbPath = settings.Value.DatabasePath;
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _db = new LiteDatabase(dbPath);

        TvShows.EnsureIndex(x => x.Name);
        Movies.EnsureIndex(x => x.Name);
        Watchlist.EnsureIndex(x => x.Status);
        PendingDownloads.EnsureIndex(x => x.Status);
        ProcessedRssItems.EnsureIndex(x => x.Guid);
        DispatchedEpisodes.EnsureIndex(x => x.ShowName);
    }

    public long? GetAuthenticatedChatId()
    {
        var session = TelegramSessions.FindOne(x => x.IsAuthenticated);
        return session?.ChatId;
    }

    public void SetAuthenticatedChat(long chatId)
    {
        var existing = TelegramSessions.FindAll().ToList();
        foreach (var s in existing)
        {
            s.IsAuthenticated = false;
            TelegramSessions.Update(s);
        }

        var session = TelegramSessions.FindOne(x => x.ChatId == chatId);
        if (session == null)
        {
            session = new TelegramSession
            {
                ChatId = chatId,
                IsAuthenticated = true,
                AuthenticatedDate = DateTime.UtcNow
            };
            TelegramSessions.Insert(session);
        }
        else
        {
            session.IsAuthenticated = true;
            session.AuthenticatedDate = DateTime.UtcNow;
            TelegramSessions.Update(session);
        }
    }

    public void Dispose() => _db.Dispose();
}
