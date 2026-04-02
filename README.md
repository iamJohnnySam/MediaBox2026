# MediaBox2026

A self-hosted **Blazor Server** media management dashboard built with **.NET 10**. MediaBox2026 automates TV show and movie tracking, torrent management, YouTube downloading, and integrates with Jellyfin and Telegram for a seamless home media server experience.

## Features

- **Media Catalog** — Automatically scans and indexes TV shows, movies, and YouTube videos from configured library paths.
- **RSS Feed Monitor** — Watches an RSS feed for new TV episode torrents and queues downloads automatically.
- **Transmission Integration** — Monitors active torrents via the Transmission RPC API and tracks download progress.
- **Download Organizer** — Moves completed downloads into the correct library folders based on parsed file names.
- **Movie Watchlist** — Maintains a movie watchlist and periodically checks for availability.
- **YouTube Downloader** — Schedules and downloads YouTube videos/streams using `yt-dlp`, with archive tracking to avoid duplicates.
- **Jellyfin Integration** — Communicates with a Jellyfin media server to trigger library refreshes.
- **Telegram Bot** — Provides a Telegram bot interface for notifications, authentication, and remote commands.
- **Crash Reporter** — Captures unhandled exceptions and error logs, saves crash data, and sends Telegram alerts.
- **In-Memory Log Viewer** — View real-time application logs from the Blazor UI.

## Tech Stack

| Component | Technology |
|---|---|
| Framework | .NET 10 / Blazor Server (Interactive SSR) |
| Database | [LiteDB](https://www.litedb.org/) (embedded NoSQL) |
| Torrent Client | [Transmission](https://transmissionbt.com/) (via RPC) |
| Media Server | [Jellyfin](https://jellyfin.org/) |
| YouTube | [yt-dlp](https://github.com/yt-dlp/yt-dlp) |
| Messaging | Telegram Bot API |

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Transmission](https://transmissionbt.com/) with RPC enabled
- [Jellyfin](https://jellyfin.org/) media server
- [yt-dlp](https://github.com/yt-dlp/yt-dlp) installed and on `PATH`
- A Telegram bot token (from [@BotFather](https://t.me/BotFather))

## Configuration

Application settings are defined in `appsettings.json` under the `MediaBox` section:

```jsonc
{
  "MediaBox": {
    "TvShowsPath": "/molecule/Media/TVShows",
    "MoviesPath": "/molecule/Media/Movies",
    "DownloadsPath": "/molecule/Media/Downloads",
    "YouTubePath": "/molecule/Media/YouTube",
    "UnknownPath": "/molecule/Media/Unknown",
    "DatabasePath": "data/mediabox.db",
    "TransmissionRpcUrl": "http://localhost:9091/transmission/rpc",
    "JellyfinUrl": "http://localhost:8096",
    "RssFeedUrl": "<your-rss-feed-url>",
    "YtDlpArchivePath": "/home/user/.config/ytdl-archive.txt",
    "CrashDataPath": "/molecule/Media/MediaBox/crashes",
    "RssFeedCheckMinutes": 30,
    "TransmissionCheckMinutes": 5,
    "DownloadOrganizerMinutes": 10,
    "WatchlistCheckHours": 6,
    "QualityWaitHours": 2,
    "MediaScanHours": 12
  }
}
```

### Secrets

Sensitive values (bot tokens, passwords, API keys) should be placed in `appsettings.Secrets.json` alongside the binary. This file is loaded automatically and is excluded from source control:

```json
{
  "MediaBox": {
    "TelegramBotToken": "<your-telegram-bot-token>",
    "AuthPassword": "<your-auth-password>",
    "TransmissionUsername": "<username>",
    "TransmissionPassword": "<password>",
    "JellyfinApiKey": "<your-jellyfin-api-key>"
  }
}
```

### Cross-Platform Development

When running on **Windows**, the application automatically remaps Linux media paths to a Windows drive letter (`M:\`) and adjusts service URLs for remote development against a Linux server. No manual configuration changes are needed to switch between environments.

## Running

```bash
# Development
dotnet run --project MediaBox2026

# Production (Linux)
dotnet publish -c Release -o ./publish
cd publish
dotnet MediaBox2026.dll
```

The application listens on `http://0.0.0.0:5000` by default.

## Project Structure

```
MediaBox2026/
├── Components/          # Blazor components and pages
├── Models/
│   └── MediaModels.cs   # Data models (TvShow, Movie, YouTubeVideo, etc.)
├── Services/
│   ├── MediaDatabase.cs           # LiteDB database wrapper
│   ├── MediaCatalogService.cs     # Filesystem scanner for media libraries
│   ├── MediaScannerService.cs     # Background service triggering periodic scans
│   ├── RssFeedMonitorService.cs   # RSS feed polling for new episodes
│   ├── TransmissionClient.cs      # Transmission RPC client
│   ├── TransmissionMonitorService.cs # Torrent progress monitor
│   ├── DownloadOrganizerService.cs# Sorts completed downloads into libraries
│   ├── MovieWatchlistService.cs   # Movie watchlist checker
│   ├── YouTubeDownloadService.cs  # Scheduled yt-dlp downloads
│   ├── JellyfinClient.cs         # Jellyfin API client
│   ├── TelegramBotService.cs     # Telegram bot + notifications
│   ├── FileNameParser.cs         # Media filename parsing utilities
│   ├── CrashReporter.cs          # Error capture and alerting
│   └── InMemoryLogProvider.cs    # In-memory log sink for the UI
├── Program.cs           # Application entry point and DI setup
├── appsettings.json     # Default configuration
└── appsettings.Secrets.json  # Sensitive config (gitignored)
```

## License

This project is for personal use.