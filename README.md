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
| Database | [SQLite](https://www.sqlite.org/) (via Microsoft.Data.Sqlite) |
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

## Telegram Bot

MediaBox2026 includes a full Telegram bot for remote control and real-time notifications from your Linux server.

### Authentication

When you first message the bot, it will prompt you for your `AuthPassword` (configured in `appsettings.Secrets.json`). Once authenticated, your chat receives all notifications and you gain access to commands. Only one chat can be authenticated at a time.

### Commands

| Command | Description |
|---|---|
| `/start` | Welcome message confirming the bot is running |
| `/help` | List all available commands |
| `/status` | System overview — TV show count, movie count, active downloads, watchlist size, YouTube count, last scan time, last RSS check |
| `/downloads` | List all active Transmission torrents with name, progress percentage, and status |
| `/watchlist` | Show pending and awaiting-confirmation items on the movie watchlist |
| `/movie <name>` | Search for a movie by name (uses YTS API). Returns results with poster, rating, genres, trailer link, and available torrent qualities |
| `/search <name>` | Alias for `/movie` |
| `/add <name>` | Quick-add a movie to the watchlist by name (parses year if included, e.g. `/add Inception 2010`) |
| `/remove <name>` | Remove a movie from the watchlist by name (fuzzy match) |
| `/scan` | Trigger an immediate full media library scan (TV shows, movies, YouTube) |

### Interactive Movie Search (`/movie`)

The `/movie` command starts an interactive browsing session:

1. Results are displayed one at a time with a **poster image**, **rating**, **genres**, **trailer link**, and **available torrent qualities/sizes**.
2. Use the inline keyboard buttons to navigate:
   - **⬅️ Prev** / **➡️ Next** — Browse through search results
   - **✅ Add to Watchlist** — Add the current movie to your watchlist (saves title, year, IMDb code, poster, and trailer)
   - **❌ Cancel** — End the search session

### Automated Notifications

MediaBox sends the following notifications to your authenticated chat automatically:

| Event | Message |
|---|---|
| **New episode download started** | `📥 New download: <title>` — When an RSS feed item matches a tracked show and is sent to Transmission |
| **Quality approval request** | `⚠️ <title> — Only <quality> available after <hours>h. Download anyway?` — Interactive Yes/No buttons when only a lower quality is available for a new episode |
| **Quality-approved download** | `📥 Downloading: <title>` — After you approve a lower-quality download |
| **TV episode organized** | `📺 Organized: <file> → <show>/Season XX/` — When a completed download is moved to the TV library |
| **Movie organized** | `🎬 Organized: <file> → <folder>/` — When a completed download is moved to the movie library |
| **Watchlist movie found** | `🎬 Found: <title> (<year>) — Quality: <quality> \| Size: <size> — Download?` — Interactive Download/Skip buttons when a watchlist movie becomes available |
| **Watchlist movie downloading** | `📥 Downloading: <title> (<year>) [<quality>]` — After you confirm a watchlist download |
| **YouTube download completed** | `📹 YouTube downloaded: <title>` — When a scheduled yt-dlp download finishes |
| **YouTube download issue** | `⚠️ YouTube issue (<title>): <error>` — When yt-dlp exits with a non-zero code |
| **Error/crash alert** | `🚨 <level>: [<service>] <message>` — When an error or critical log entry is detected |
| **Unhandled exception** | `💀 CRITICAL [<source>]: <message>` — On unhandled exceptions, with crash data saved to disk |

## Project Structure

```
MediaBox2026/
├── Components/          # Blazor components and pages
├── Models/
│   └── MediaModels.cs   # Data models (TvShow, Movie, YouTubeVideo, etc.)
├── Services/
│   ├── MediaDatabase.cs           # SQLite database wrapper
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

## Future Improvements (TODO)

A roadmap of enhancements to evolve MediaBox into a fully personalized Linux server assistant.

### Telegram Bot Enhancements
- [ ] `/delete <torrent>` — Remove a torrent (with optional data deletion) directly from Telegram
- [ ] `/pause` / `/resume` — Pause and resume individual torrents
- [ ] `/logs` — Retrieve recent application logs via Telegram
- [ ] `/restart` — Trigger a graceful application restart remotely
- [ ] `/disk` — Report disk usage for each media library path
- [ ] `/upcoming` — Show expected upcoming episodes for tracked TV shows
- [ ] Multi-user Telegram support — Allow multiple authenticated chats with role-based permissions (admin/viewer)
- [ ] Notification preferences — Per-user toggleable notification categories (downloads, errors, scans, etc.)

### Media Management
- [ ] Subtitle auto-download — Integrate with OpenSubtitles or Bazarr for automatic subtitle fetching
- [ ] Media metadata enrichment — Pull poster art, descriptions, and ratings from TMDb/OMDb for the Blazor dashboard
- [ ] Duplicate detection — Identify and flag duplicate media files across libraries
- [ ] Storage analytics — Dashboard showing disk usage trends, largest files, and library growth over time
- [ ] Multi-quality management — Keep preferred quality and auto-upgrade when better versions appear
- [ ] Watch history tracking — Track what has been watched via Jellyfin webhooks

### Automation & Scheduling
- [ ] Smart RSS rules — Configurable per-show quality preferences and auto-approve thresholds
- [ ] Calendar integration — Sync TV show air dates and download schedules with a calendar
- [ ] Scheduled maintenance tasks — Auto-cleanup of old crash data, orphaned files, and stale database entries
- [ ] Retry failed downloads — Automatic retry logic for failed torrent additions with exponential backoff
- [ ] Configurable news sources — Add/remove YouTube news sources via the Blazor UI instead of `appsettings.json`

### Server & Infrastructure
- [ ] Health check endpoint — `/health` API for external uptime monitoring (e.g., Uptime Kuma)
- [ ] Docker container — Dockerfile and `docker-compose.yml` for one-command deployment
- [ ] Systemd service file — Ready-to-use `.service` file for Linux auto-start on boot
- [ ] Backup & restore — Scheduled SQLite database backups with retention policy
- [ ] VPN/proxy support — Route torrent traffic through a configurable VPN or SOCKS proxy
- [ ] Webhook support — Outgoing webhooks for integration with Home Assistant, Discord, or other services

### Blazor Dashboard
- [ ] Real-time torrent progress — Live progress bars for active downloads on the dashboard
- [ ] Search and filter — Full-text search across all media libraries from the UI
- [ ] Mobile-friendly layout — Responsive design optimized for phone access
- [ ] Settings page — Edit `MediaBoxSettings` from the Blazor UI without restarting
- [ ] Activity timeline — Visual timeline of all downloads, scans, and organized files
- [ ] Dark/light theme toggle

### Intelligence & Personalization
- [ ] Recommendation engine — Suggest movies/shows based on your library and watch history
- [ ] Natural language commands — Allow Telegram messages like "download the latest episode of X" without strict command syntax
- [ ] Smart scheduling — Learn your viewing patterns and prioritize downloads accordingly
- [ ] Custom Telegram alerts — User-defined rules like "notify me when any 4K movie is available"
- [ ] Voice message support — Process Telegram voice messages for hands-free commands

## License

This project is for personal use.