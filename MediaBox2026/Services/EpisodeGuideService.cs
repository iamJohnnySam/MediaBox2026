using System.Collections.Concurrent;
using System.Text.Json;
using MediaBox2026.Models;
using Microsoft.Extensions.Options;

namespace MediaBox2026.Services;

/// <summary>
/// Answers "which episodes of this show aired, and which of them do I have?" and
/// "what can I download for the ones I don't?".
///
/// Two keyless public APIs, both already proven in this codebase's style:
///   - TVmaze (`singlesearch/shows?q=X&embed=episodes`) — the aired-episode list. MediaCatalogService
///     already calls TVmaze for year lookups, so this adds no new provider, and the `embed` gets the
///     show + every episode in ONE request.
///   - EZTV (`api/get-torrents?imdb_id=N`) — per-show torrent list with seeds/peers/size/magnet.
///     Keyed by the IMDb id TVmaze hands back in the same call.
///
/// Both are cached in memory (see the TTLs below) because the UI hits this once per show row and then
/// once per magnet click — without the cache, opening five episodes of one show would be five
/// identical EZTV fetches. Nothing is persisted: this is a lookup aid, not state.
/// </summary>
public class EpisodeGuideService(
    MediaDatabase db,
    MediaCatalogService catalog,
    TransmissionClient transmission,
    IHttpClientFactory httpFactory,
    IOptionsMonitor<MediaBoxSettings> settings,
    ILogger<EpisodeGuideService> logger)
{
    private static readonly TimeSpan GuideTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan TorrentTtl = TimeSpan.FromMinutes(30);

    // EZTV pages 100 at a time and has no season/episode filter, so we pull the show's list and
    // filter locally. 3 pages = 300 torrents, which covers every show in the library today.
    private const int MaxTorrentPages = 3;

    private readonly ConcurrentDictionary<string, (DateTime At, ShowGuide Value)> _guides = new();
    private readonly ConcurrentDictionary<string, (DateTime At, List<EztvTorrent> Value)> _torrents = new();

    // ── Guide ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every aired episode of the show, each flagged with whether it's on disk.
    /// <paramref name="folderPath"/> identifies the show exactly (it's what the library list shows);
    /// <paramref name="name"/> is the fallback and the TVmaze search term.
    /// </summary>
    public async Task<ShowGuide> GetGuideAsync(string folderPath, string name, CancellationToken ct = default)
    {
        var show = ResolveShow(folderPath, name);
        if (show == null)
            return ShowGuide.Failed($"Show not in the library: {name}");

        var aired = await GetAiredAsync(show.Name, ct);
        if (aired.Error.Length > 0)
            return aired;

        // The have/missing diff — the whole point of this service. Local episodes come from
        // filename parsing (MediaCatalogService.ScanTvShows), so this is disk truth, not a download log.
        var owned = show.Episodes.Select(e => (e.Season, e.Episode)).ToHashSet();
        var merged = aired.Episodes
            .Select(e => e with { Have = owned.Contains((e.Season, e.Episode)) })
            .ToList();

        return aired with { Show = show.Name, Episodes = merged };
    }

    private TvShow? ResolveShow(string folderPath, string name)
    {
        if (!string.IsNullOrWhiteSpace(folderPath))
        {
            var byPath = db.TvShows.FindAll().FirstOrDefault(s => s.FolderPath == folderPath);
            if (byPath != null) return byPath;
        }
        return catalog.FindTvShow(name);
    }

    /// <summary>TVmaze show + episode list, cached. No have/missing flags — that's the caller's join.</summary>
    private async Task<ShowGuide> GetAiredAsync(string name, CancellationToken ct)
    {
        var key = name.ToLowerInvariant();
        if (_guides.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < GuideTtl)
            return hit.Value;

        try
        {
            using var http = httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(15);

            var url = $"{settings.CurrentValue.TvMazeApiUrl.TrimEnd('/')}" +
                      $"/singlesearch/shows?q={Uri.EscapeDataString(name)}&embed=episodes";
            logger.LogDebug("Episode guide API call: {Url}", url);

            var response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
                return ShowGuide.Failed($"TVmaze returned {(int)response.StatusCode} for \"{name}\"");

            var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);

            var imdb = "";
            if (json.TryGetProperty("externals", out var ext) &&
                ext.TryGetProperty("imdb", out var imdbProp) && imdbProp.ValueKind == JsonValueKind.String)
                imdb = imdbProp.GetString()!.TrimStart('t');

            var matched = json.TryGetProperty("name", out var n) ? n.GetString() ?? name : name;
            var premiered = json.TryGetProperty("premiered", out var p) ? p.GetString() ?? "" : "";

            var episodes = new List<AiredEpisode>();
            if (json.TryGetProperty("_embedded", out var emb) && emb.TryGetProperty("episodes", out var eps))
            {
                foreach (var e in eps.EnumerateArray())
                {
                    // Specials carry a null number and no season slot we can diff against a filename.
                    if (!e.TryGetProperty("number", out var num) || num.ValueKind != JsonValueKind.Number) continue;
                    if (!e.TryGetProperty("season", out var seasonProp) || seasonProp.ValueKind != JsonValueKind.Number) continue;

                    var airdate = e.TryGetProperty("airdate", out var ad) ? ad.GetString() ?? "" : "";
                    // "Aired" is the difference between missing and simply not out yet — a not-yet-aired
                    // episode must never be reported as a hole in the collection.
                    var hasAired = e.TryGetProperty("airstamp", out var stamp) &&
                                   DateTime.TryParse(stamp.GetString(), out var when) &&
                                   when <= DateTime.UtcNow;

                    episodes.Add(new AiredEpisode(
                        seasonProp.GetInt32(),
                        num.GetInt32(),
                        e.TryGetProperty("name", out var t) ? t.GetString() ?? "" : "",
                        airdate,
                        hasAired,
                        Have: false));
                }
            }

            var guide = new ShowGuide(matched, imdb, premiered, episodes, "");
            _guides[key] = (DateTime.UtcNow, guide);
            logger.LogInformation("Episode guide for '{Name}' -> TVmaze '{Matched}' (imdb {Imdb}): {Count} episodes",
                name, matched, imdb.Length > 0 ? imdb : "none", episodes.Count);
            return guide;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Episode guide lookup failed for: {Name}", name);
            return ShowGuide.Failed($"Episode guide lookup failed: {ex.Message}");
        }
    }

    // ── Torrent search ───────────────────────────────────────────────────────

    /// <summary>
    /// Download candidates for one episode, best-seeded first. Season packs that contain the episode
    /// are included after the single-episode releases — they're a valid answer, just a bigger one.
    /// </summary>
    public async Task<(List<TorrentChoice> Choices, string Error)> SearchEpisodeAsync(
        string imdbId, int season, int episode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(imdbId))
            return ([], "No IMDb id for this show — TVmaze didn't have one, so EZTV can't be queried.");

        var (all, error) = await GetShowTorrentsAsync(imdbId, ct);
        if (error.Length > 0) return ([], error);

        var choices = all
            .Where(t => t.Season == season && (t.Episode == episode || t.Episode == 0))
            .Select(t => new TorrentChoice(
                t.Title,
                FileNameParser.DetectQuality(t.Title) ?? "unknown",
                t.Seeds,
                t.Peers,
                t.SizeBytes,
                t.Magnet,
                SeasonPack: t.Episode == 0))
            .Select(c => c with { MeetsStandard = FileNameParser.IsQualityAcceptable(c.Quality == "unknown" ? null : c.Quality) })
            .OrderBy(c => c.SeasonPack)
            .ThenByDescending(c => c.Seeds)
            .ToList();

        return (choices, choices.Count == 0 ? $"No releases found for S{season:00}E{episode:00}." : "");
    }

    /// <summary>All EZTV torrents for a show, cached per IMDb id (one fetch serves every episode).</summary>
    private async Task<(List<EztvTorrent> Items, string Error)> GetShowTorrentsAsync(string imdbId, CancellationToken ct)
    {
        if (_torrents.TryGetValue(imdbId, out var hit) && DateTime.UtcNow - hit.At < TorrentTtl)
            return (hit.Value, "");

        var items = new List<EztvTorrent>();
        try
        {
            using var http = httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(20);
            http.DefaultRequestHeaders.Add("User-Agent", "MediaBox2026/1.0 (Episode Guide)");

            var baseUrl = settings.CurrentValue.EztvApiUrl;
            for (var page = 1; page <= MaxTorrentPages; page++)
            {
                var url = $"{baseUrl}{(baseUrl.Contains('?') ? "&" : "?")}imdb_id={Uri.EscapeDataString(imdbId)}&limit=100&page={page}";
                logger.LogDebug("Torrent search API call: {Url}", url);

                var response = await http.GetAsync(url, ct);
                if (!response.IsSuccessStatusCode)
                {
                    if (page == 1) return ([], $"EZTV returned {(int)response.StatusCode}.");
                    break;
                }

                var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
                if (!json.TryGetProperty("torrents", out var arr) || arr.ValueKind != JsonValueKind.Array) break;

                var before = items.Count;
                foreach (var t in arr.EnumerateArray())
                    items.Add(ParseTorrent(t));

                if (items.Count - before < 100) break; // last page
            }

            _torrents[imdbId] = (DateTime.UtcNow, items);
            logger.LogInformation("Torrent search for imdb {Imdb}: {Count} releases", imdbId, items.Count);
            return (items, "");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Torrent search failed for imdb {Imdb}", imdbId);
            return ([], $"Torrent search failed: {ex.Message}");
        }
    }

    /// <summary>EZTV sends season/episode/size as strings, so every field here is parsed defensively.</summary>
    private static EztvTorrent ParseTorrent(JsonElement t)
    {
        static string Str(JsonElement e, string prop) =>
            e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        static int Num(JsonElement e, string prop) =>
            e.TryGetProperty(prop, out var v)
                ? v.ValueKind == JsonValueKind.Number ? v.GetInt32() : int.TryParse(v.GetString(), out var i) ? i : 0
                : 0;

        var magnet = Str(t, "magnet_url");
        if (magnet.Length == 0) magnet = Str(t, "torrent_url");

        return new EztvTorrent(
            Str(t, "title"),
            Num(t, "season"),
            Num(t, "episode"),
            Num(t, "seeds"),
            Num(t, "peers"),
            long.TryParse(Str(t, "size_bytes"), out var size) ? size : 0,
            magnet);
    }

    // ── Add ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Hands a chosen magnet to Transmission and tombstones the episode as dispatched, so the RSS
    /// monitor's dedupe (RssFeedMonitorService) doesn't queue the same episode again before the
    /// download lands and the next scan sees it on disk.
    /// </summary>
    public async Task<(bool Ok, string Message)> AddAsync(
        string magnet, string showName, int season, int episode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(magnet))
            return (false, "No torrent link.");

        var added = await transmission.AddTorrentAsync(magnet, ct);
        if (!added) return (false, "Transmission rejected the torrent (is it running?).");

        if (season > 0 && episode > 0 && !string.IsNullOrWhiteSpace(showName) &&
            !db.DispatchedEpisodes.Exists(d => d.ShowName == showName && d.Season == season && d.Episode == episode))
        {
            db.DispatchedEpisodes.Insert(new DispatchedEpisode
            {
                ShowName = showName,
                Season = season,
                Episode = episode,
                DispatchedDate = DateTime.UtcNow
            });
        }

        logger.LogInformation("📥 Manual episode download: {Show} S{Season}E{Episode}", showName, season, episode);
        return (true, $"Added to Transmission: {showName} S{season:00}E{episode:00}");
    }

    private record EztvTorrent(string Title, int Season, int Episode, int Seeds, int Peers, long SizeBytes, string Magnet);
}

public record AiredEpisode(int Season, int Episode, string Title, string AirDate, bool Aired, bool Have);

public record ShowGuide(string Show, string ImdbId, string Premiered, List<AiredEpisode> Episodes, string Error)
{
    public static ShowGuide Failed(string error) => new("", "", "", [], error);
}

public record TorrentChoice(
    string Title, string Quality, int Seeds, int Peers, long SizeBytes, string Magnet, bool SeasonPack)
{
    /// <summary>True when the release is within MediaBox's quality standard (<=720p, FileNameParser.IsQualityAcceptable).</summary>
    public bool MeetsStandard { get; init; }
}
