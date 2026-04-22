using Microsoft.Extensions.Logging;

namespace MediaBox2026.Models;

public interface IEntity
{
    int Id { get; set; }
}

public class TvShow : IEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int? Year { get; set; }
    public string FolderPath { get; set; } = "";
    public int LatestSeason { get; set; }
    public int LatestEpisode { get; set; }
    public List<EpisodeInfo> Episodes { get; set; } = [];
    public DateTime LastScanned { get; set; }
}

public class EpisodeInfo
{
    public int Season { get; set; }
    public int Episode { get; set; }
    public string FileName { get; set; } = "";
}

public class Movie : IEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int? Year { get; set; }
    public string FolderPath { get; set; } = "";
    public string FileName { get; set; } = "";
    public DateTime LastScanned { get; set; }
}

public class YouTubeVideo : IEntity
{
    public int Id { get; set; }
    public string Uploader { get; set; } = "";
    public string Title { get; set; } = "";
    public string FileName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public DateTime CatalogedDate { get; set; }
}

public class WatchlistItem : IEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int? Year { get; set; }
    public WatchlistStatus Status { get; set; } = WatchlistStatus.Pending;
    public DateTime AddedDate { get; set; }
    public string? TorrentUrl { get; set; }
    public string? Quality { get; set; }
    public string? ImdbCode { get; set; }
    public string? PosterUrl { get; set; }
    public string? TrailerCode { get; set; }
}

public enum WatchlistStatus
{
    Pending,
    Found,
    AwaitingConfirmation,
    Downloading,
    Downloaded,
    Cancelled
}

public class PendingDownload : IEntity
{
    public int Id { get; set; }
    public string RssTitle { get; set; } = "";
    public string TorrentUrl { get; set; } = "";
    public string? Quality { get; set; }
    public string ShowName { get; set; } = "";
    public int Season { get; set; }
    public int Episode { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime? RssPublishDate { get; set; }
    public int CheckCount { get; set; }
    public bool AskedUser { get; set; }
    public DateTime? LastAsked { get; set; }
    public int? TelegramMessageId { get; set; }
    public PendingStatus Status { get; set; } = PendingStatus.WaitingForQuality;
}

public enum PendingStatus
{
    WaitingForQuality,
    Approved,
    Rejected,
    Downloaded
}

public class ProcessedRssItem : IEntity
{
    public int Id { get; set; }
    public string Guid { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTime ProcessedDate { get; set; }
}

public class TelegramSession
{
    public int Id { get; set; }
    public long ChatId { get; set; }
    public bool IsAuthenticated { get; set; }
    public DateTime AuthenticatedDate { get; set; }
}

public class NotifiedDuplicate : IEntity
{
    public int Id { get; set; }
    public string ShowName { get; set; } = "";
    public string FolderNames { get; set; } = "";
    public DateTime NotifiedDate { get; set; }
}

public class TorrentInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public double PercentDone { get; set; }
    public int Status { get; set; }
    public long TotalSize { get; set; }
    public string? DownloadDir { get; set; }
    public long DateAdded { get; set; }
    public bool IsFinished => PercentDone >= 1.0;

    public string StatusText => Status switch
    {
        0 => "Stopped",
        1 => "Check Pending",
        2 => "Checking",
        3 => "Download Pending",
        4 => "Downloading",
        5 => "Seed Pending",
        6 => "Seeding",
        _ => "Unknown"
    };
}

public class ParsedMediaInfo
{
    public string CleanName { get; set; } = "";
    public int? Year { get; set; }
    public int? Season { get; set; }
    public int? Episode { get; set; }
    public string? Quality { get; set; }
    public bool IsTvShow => Season.HasValue && Episode.HasValue;
    public string OriginalFileName { get; set; } = "";
}

public class MediaBoxSettings
{
    public string TelegramBotToken { get; set; } = "";
    public long? TelegramChatId { get; set; }
    public string AuthPassword { get; set; } = "changeme";

    public string TvShowsPath { get; set; } = "/molecule/Media/TVShows";
    public string MoviesPath { get; set; } = "/molecule/Media/Movies";
    public string DownloadsPath { get; set; } = "/molecule/Media/Downloads";
    public string YouTubePath { get; set; } = "/molecule/Media/YouTube";
    public string UnknownPath { get; set; } = "/molecule/Media/Unknown";
    public string DatabasePath { get; set; } = "data/mediabox.db";

    public string TransmissionRpcUrl { get; set; } = "http://localhost:9091/transmission/rpc";
    public string? TransmissionUsername { get; set; }
    public string? TransmissionPassword { get; set; }

    public string JellyfinUrl { get; set; } = "";
    public string JellyfinApiKey { get; set; } = "";

    public string CrashDataPath { get; set; } = "/molecule/Media/MediaBox/crashes";

    public string RssFeedUrl { get; set; } = "https://episodefeed.com/rss/129/477500a959d617288def89205dd3d6bacf97380e";

    public string YtDlpArchivePath { get; set; } = "/home/atom/.config/ytdl-archive.txt";

    public List<NewsSource> NewsSources { get; set; } =
    [
        new() { Url = "https://www.youtube.com/@NewsFirstSrilanka/streams", MatchTitle = "Prime Time Sinhala News - 7 PM", DownloadTime = "19:45" },
        new() { Url = "https://www.youtube.com/@NewsFirstSrilanka/stream", MatchTitle = "Prime Time English News - 9 PM", DownloadTime = "21:45" }
    ];

    public int RssFeedCheckMinutes { get; set; } = 30;
    public int TransmissionCheckMinutes { get; set; } = 5;
    public int DownloadOrganizerMinutes { get; set; } = 10;
    public int WatchlistCheckHours { get; set; } = 6;
    public int QualityWaitHours { get; set; } = 4;
    public int MediaScanHours { get; set; } = 12;
}

public class NewsSource
{
    public string Url { get; set; } = "";
    public string MatchTitle { get; set; } = "";
    public string DownloadTime { get; set; } = "19:45";
}

public class DispatchedEpisode : IEntity
{
    public int Id { get; set; }
    public string ShowName { get; set; } = "";
    public int Season { get; set; }
    public int Episode { get; set; }
    public DateTime DispatchedDate { get; set; }
}

public class PendingLargeTorrent : IEntity
{
    public int Id { get; set; }
    public int TorrentId { get; set; }
    public string TorrentName { get; set; } = "";
    public long TotalSize { get; set; }
    public DateTime AddedDate { get; set; }
    public bool AskedUser { get; set; }
    public LargeTorrentStatus Status { get; set; } = LargeTorrentStatus.Paused;
}

public enum LargeTorrentStatus
{
    Paused,
    Approved,
    Rejected
}

public class LogEntry
{
    public DateTime Timestamp { get; set; }
    public LogLevel Level { get; set; }
    public string Category { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Exception { get; set; }
}

public class InlineButton
{
    public string Text { get; set; } = "";
    public string CallbackData { get; set; } = "";
}

public class RssFeedSubscription : IEntity
{
    public int Id { get; set; }
    public string FeedUrl { get; set; } = "";
    public string FeedName { get; set; } = "";
    public DateTime SubscribedDate { get; set; }
    public DateTime? LastChecked { get; set; }
    public bool IsActive { get; set; } = true;
}

public class ProcessedFeedItem : IEntity
{
    public int Id { get; set; }
    public int SubscriptionId { get; set; }
    public string ItemGuid { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTime ProcessedDate { get; set; }
}
