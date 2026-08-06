# MediaBox2026

## Environment

- **Dev source:** `/home/atom/dev/MediaBox2026/`
- **Production binary:** `/home/atom/MediaBox2026/` (what systemd runs)
- **Runtime user:** `atom`
- **Service:** `mediabox.service`
- **Web UI:** `http://192.168.1.30:5000` (auth password in `appsettings.Secrets.json`)
- **GitHub remote:** `https://github.com/iamJohnnySam/MediaBox2026.git`

---

## Solution Structure

```
MediaBox2026/                        ← git repo root
├── MediaBox2026.slnx                ← solution file
├── CLAUDE.md
├── deploy.sh                        ← build + deploy script
└── MediaBox2026/                    ← project directory
    ├── MediaBox2026.csproj
    ├── Program.cs                   ← startup, DI, background service registration
    ├── appsettings.json             ← all paths, URLs, intervals (safe to commit)
    ├── appsettings.Secrets.json     ← tokens, passwords (gitignored — never commit)
    ├── Components/                  ← Razor/Blazor UI components
    ├── Models/
    │   └── MediaModels.cs           ← all domain models
    ├── Services/
    │   ├── RssFeedMonitorService.cs ← RSS polling (every 30 min)
    │   ├── TransmissionMonitorService.cs ← torrent status (every 5 min)
    │   ├── DownloadOrganizerService.cs   ← moves completed downloads (every 10 min)
    │   ├── MediaScannerService.cs   ← scans library (every 12 hr)
    │   ├── YouTubeDownloadService.cs ← yt-dlp news downloads
    │   ├── JellyfinClient.cs        ← triggers Jellyfin library scan
    │   ├── TransmissionClient.cs    ← Transmission RPC wrapper
    │   ├── MediaDatabase.cs         ← SQLite access layer
    │   ├── EpisodeGuideService.cs    ← aired-episode guide (TVmaze) + per-episode torrent search (EZTV)
    │   ├── MediaCatalogService.cs
    │   ├── MovieWatchlistService.cs
    │   ├── NewsRssFeedService.cs
    │   ├── TelegramExtensions.cs
    │   └── ...
    ├── data/
    │   └── mediabox.db              ← SQLite database (gitignored)
    └── wwwroot/
```

---

## Build & Run

```bash
# Build only
dotnet build /home/atom/dev/MediaBox2026/MediaBox2026/MediaBox2026.csproj

# Run locally (dev mode, port 5000)
dotnet run --project /home/atom/dev/MediaBox2026/MediaBox2026/MediaBox2026.csproj

# Publish (Linux single-file, framework-dependent — matches production binary format)
dotnet publish /home/atom/dev/MediaBox2026/MediaBox2026/MediaBox2026.csproj \
  -c Release -r linux-x64 -p:PublishSingleFile=true --self-contained false \
  -o /home/atom/dev/MediaBox2026/publish
```

---

## Deploy to Production

```bash
# Full deploy: publish → copy → restart service
bash /home/atom/dev/MediaBox2026/deploy.sh
```

What the script does:
1. `dotnet publish` → `/home/atom/dev/MediaBox2026/publish/`
2. Copies output to `/home/atom/MediaBox2026/` (preserves `data/` and `appsettings.Secrets.json`)
3. `sudo systemctl restart mediabox`

---

## Service Management

```bash
sudo systemctl status mediabox
sudo systemctl restart mediabox
sudo systemctl stop mediabox
sudo journalctl -u mediabox -f          # live logs
sudo journalctl -u mediabox -n 100      # last 100 lines
```

---

## Secrets & Config

| File | Purpose | Committed? |
|---|---|---|
| `appsettings.json` | Paths, URLs, intervals, paused flags | Yes |
| `appsettings.Secrets.json` | Telegram token, Jellyfin API key, auth password, Transmission credentials | **No** (gitignored) |

Secrets file lives at both `/home/atom/dev/MediaBox2026/MediaBox2026/appsettings.Secrets.json` (dev) and `/home/atom/MediaBox2026/appsettings.Secrets.json` (prod). The deploy script preserves the prod copy — never overwrite it.

---

## Key Config (appsettings.json)

- `TvShowsPath` → `/molecule/Media/TVShows`
- `MoviesPath` → `/molecule/Media/Movies`
- `DownloadsPath` → `/molecule/Media/Downloads`
- `YouTubePath` → `/molecule/Media/YouTube`
- `DatabasePath` → `data/mediabox.db` (relative to binary)
- `TransmissionRpcUrl` → `http://localhost:9091/transmission/rpc`
- `JellyfinUrl` → `http://localhost:8096`
- `YtDlpArchivePath` → `/home/atom/.config/ytdl-archive.txt`

---

## Episode guide (what aired vs. what's on disk)

`EpisodeGuideService` backs Tower's Library tab (`/apps/mediabox` → Library → click a TV row).
Three gRPC RPCs: `GetShowEpisodes`, `SearchEpisodeTorrents`, `AddEpisodeTorrent`.

- **Aired list = TVmaze**, one call: `singlesearch/shows?q=NAME&embed=episodes` returns the show,
  its IMDb id and every episode together. Keyless. Base URL is `TvMazeApiUrl` in settings.
- **Have/missing = the local diff**, `TvShow.Episodes` (filename-parsed by `MediaCatalogService`)
  vs. the TVmaze list. An episode that hasn't aired yet is flagged `aired=false` and is **not**
  reported missing — that distinction is the point, otherwise every running show looks incomplete.
- **Torrents = EZTV** (`EztvApiUrl`, keyed by the IMDb id, `tt` stripped). EZTV has no
  season/episode filter, so the service pulls up to 3 pages of 100 and filters locally; the list is
  cached per show for 30 min so opening several episodes costs one fetch. Season packs (EZTV sends
  `episode: 0`) are returned after the single-episode releases, flagged.
- **Quality** is `FileNameParser.DetectQuality` + `IsQualityAcceptable` — the same <=720p rule the
  RSS monitor auto-downloads under. Above-standard releases are still listed, just flagged; the
  choice is the user's here, unlike the automated path.
- **Tombstones are read, not just written.** `DispatchedEpisodes` rows (RSS dispatch, a deliberate
  delete caught by `TombstoneRemovedEpisodes`, or the guide's own 🚫 button) mark an episode
  `dispatched` — the UI shows "↻ Queued" instead of "✗ Missing", because re-downloading it is exactly
  what the tombstone exists to prevent. Adding a torrent from the guide writes one too.
  The 🚫/↺ button on a row adds or lifts a tombstone by hand (`SetEpisodeIgnored`), which is how you
  tell the RSS monitor to never fetch an episode.
- **Season-wide controls** (`SetSeasonIgnored`, and a season search): ignoring a season tombstones
  every episode of it not already on disk, *including ones that haven't aired yet* — "ignore this
  season" has to cover the announced remainder or the next RSS post walks straight past it. Episodes
  on disk are skipped; a tombstone there says nothing the file doesn't. The season torrent search is
  the same `SearchEpisodeTorrents` with `episode = 0`: EZTV tags season packs as episode 0, so the
  existing filter falls through to packs only — no second code path.
- **Tombstone `ShowName` is inconsistent by origin** — the RSS monitor writes the parsed release
  title, the catalog writes the library folder name — and `DbCollection.Exists` is an ordinal C#
  compare. Both the read and the un-ignore delete therefore match with `FuzzyMatch >= 0.5`, the same
  same-show threshold as `FindTvShow`. An `==` would silently miss most rows.
- **EZTV moves domain** (eztvx.to → eztv.re → …). When searches start returning nothing, the fix is
  `EztvApiUrl` in `appsettings.json`, not code.

## Knowledge Graph (RAG)

A pre-built knowledge graph lives in `graphify-out/`. Use it as the first stop when exploring code structure, finding where things are defined, or tracing relationships.

- **`graphify-out/graph.json`** — full node/edge graph. Query for file relationships, symbol references, and community clusters before searching raw files.
- When asking "where is X", "what calls Y", "blast radius of changing Z" — check the graph first.

To refresh after significant changes: `/graphify`
