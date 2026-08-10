using System.Text.Json;
using MediaBox2026.Models;
using Microsoft.Extensions.Options;

namespace MediaBox2026.Services;

public class MovieWatchlistService(
    MediaDatabase db,
    MediaCatalogService catalog,
    TransmissionClient transmission,
    ITelegramNotifier telegram,
    MediaBoxState state,
    IOptionsMonitor<MediaBoxSettings> settings,
    IHttpClientFactory httpFactory,
    ILogger<MovieWatchlistService> logger) : BackgroundService
{
    private int _consecutiveFailures = 0;
    private const int MaxConsecutiveFailures = 5;
    private DateTime _lastApiCall = DateTime.MinValue;
    private const int MinApiCallIntervalMs = 1000; // Rate limit: max 1 call per second

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("🎬 Movie watchlist waiting for Telegram readiness...");

        try
        {
            await state.WaitForTelegramReadyAsync(ct);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Movie watchlist cancelled during Telegram wait");
            return;
        }

        await Task.Delay(TimeSpan.FromMinutes(2), ct);
        logger.LogInformation("🚀 Movie watchlist service started");
        logger.LogInformation("Check interval: {Hours} hours", settings.CurrentValue.WatchlistCheckHours);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(ct);
                _consecutiveFailures = 0; // Reset on success
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                logger.LogInformation("🛑 Movie watchlist shutting down...");
                break;
            }
            catch (HttpRequestException hex)
            {
                _consecutiveFailures++;
                logger.LogError(hex, "❌ Watchlist HTTP error (consecutive failures: {Count}/{Max})", _consecutiveFailures, MaxConsecutiveFailures);

                if (_consecutiveFailures >= MaxConsecutiveFailures)
                {
                    logger.LogCritical("🚨 Movie watchlist reached max consecutive failures. Increasing retry delay.");
                    await Task.Delay(TimeSpan.FromHours(settings.CurrentValue.WatchlistCheckHours * 2), ct);
                    _consecutiveFailures = 0;
                    continue;
                }
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                logger.LogError(ex, "❌ Watchlist check error (consecutive failures: {Count}/{Max})", _consecutiveFailures, MaxConsecutiveFailures);
            }

            try
            {
                await Task.Delay(TimeSpan.FromHours(settings.CurrentValue.WatchlistCheckHours), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                logger.LogInformation("🛑 Movie watchlist shutting down during delay...");
                break;
            }
        }
    }

    /// <summary>
    /// Runs one watchlist check cycle. This is the per-cycle work that ExecuteAsync's loop runs
    /// on a timer; it's also callable directly (e.g. via gRPC trigger).
    /// </summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        logger.LogInformation("=== Watchlist Check Cycle Starting ===");
        if (_consecutiveFailures > 0)
        {
            logger.LogWarning("⚠️ Consecutive failures: {Count}/{Max}", _consecutiveFailures, MaxConsecutiveFailures);
        }

        var checkStart = DateTime.UtcNow;
        await CheckWatchlistAsync(ct);

        var duration = DateTime.UtcNow - checkStart;
        logger.LogInformation("✅ Watchlist check cycle completed in {Duration:F1}s", duration.TotalSeconds);
    }

    private async Task CheckWatchlistAsync(CancellationToken ct)
    {
        var pending = db.Watchlist
            .Find(w => w.Status == WatchlistStatus.Pending)
            .ToList();

        if (pending.Count == 0)
        {
            logger.LogDebug("No pending watchlist items found");
            return;
        }

        logger.LogInformation("🔍 Checking {Count} pending watchlist item(s)", pending.Count);

        using var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.Add("User-Agent", "MediaBox2026/1.0 (Movie Watchlist)");

        var foundCount = 0;
        var processedCount = 0;

        foreach (var item in pending)
        {
            processedCount++;
            try
            {
                // Already on disk — nothing to fetch. Checked every cycle rather than only at add
                // time, because the film can land from any other release (or by hand) afterwards;
                // that is how a watchlist item outlived a copy already sitting in the library.
                var owned = catalog.FindMovie(item.Name, item.Year);

                // FindMovie's 0.6 fuzzy threshold scores "The Angry Birds Movie" against
                // "The Angry Birds Movie 2" at 0.90 — fine for filing a downloaded file, far too
                // loose for silently cancelling a download, since a sequel differs by exactly that
                // one trailing token. So the year has to agree (FindMovie already filters on it
                // when known), or the title has to match outright.
                // ponytail: a yearless item therefore still needs an exact title; it falls through
                // to the search and may re-fetch a film already held. Failing that way round is the
                // safe one — resolve the year on add if that ever becomes the common case.
                if (owned != null && !item.Year.HasValue &&
                    !string.Equals(owned.Name, item.Name, StringComparison.OrdinalIgnoreCase))
                    owned = null;

                if (owned != null)
                {
                    logger.LogInformation("📚 '{Name}' already in library as '{Owned}' ({Year}) — closing watchlist item, nothing searched",
                        item.Name, owned.Name, owned.Year);
                    item.Status = WatchlistStatus.Downloaded;
                    if (!item.Year.HasValue) item.Year = owned.Year;
                    db.Watchlist.Update(item);
                    state.WatchlistCount = db.Watchlist.Count(w => w.Status == WatchlistStatus.Pending);
                    state.NotifyChange();
                    await telegram.SendMessageAsync(
                        $"📚 Already in library: {owned.Name}{(owned.Year.HasValue ? $" ({owned.Year})" : "")}\n\n" +
                        $"Removed from watchlist — nothing downloaded.", ct);
                    continue;
                }

                // Rate limiting
                var timeSinceLastCall = DateTime.UtcNow - _lastApiCall;
                if (timeSinceLastCall.TotalMilliseconds < MinApiCallIntervalMs)
                {
                    var delay = MinApiCallIntervalMs - (int)timeSinceLastCall.TotalMilliseconds;
                    logger.LogDebug("Rate limiting: waiting {Delay}ms before next API call", delay);
                    await Task.Delay(delay, ct);
                }

                _lastApiCall = DateTime.UtcNow;
                logger.LogDebug("Checking [{Current}/{Total}]: {Name}", processedCount, pending.Count, item.Name);
                var query = item.Year.HasValue ? $"{item.Name} {item.Year}" : item.Name;
                var url = $"https://yts.bz/api/v2/list_movies.json?query_term={Uri.EscapeDataString(query)}&limit=5";

                logger.LogDebug("🌐 API call: {Url}", url);

                var response = await http.GetAsync(url, ct);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("⚠️ YTS API returned {StatusCode} for: {Name}", response.StatusCode, item.Name);
                    continue;
                }

                var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
                if (!json.TryGetProperty("data", out var data) ||
                    !data.TryGetProperty("movies", out var movies) ||
                    movies.GetArrayLength() == 0)
                {
                    logger.LogDebug("No results found for: {Name}", item.Name);
                    continue;
                }

                YtsResult? bestMatch = null;   // acceptable quality — take it without asking
                YtsResult? highMatch = null;   // above standard — goes through the wait/approve clock
                var matchCount = 0;
                foreach (var movie in movies.EnumerateArray())
                {
                    var title = movie.GetProperty("title").GetString() ?? "";
                    var year = movie.GetProperty("year").GetInt32();
                    var score = FileNameParser.FuzzyMatch(title, item.Name);

                    logger.LogDebug("Match score {Score:F2} for '{Title}' ({Year})", score, title, year);

                    if (score < 0.5) continue;
                    if (item.Year.HasValue && year != item.Year.Value) continue;

                    matchCount++;

                    if (!movie.TryGetProperty("torrents", out var torrents)) continue;

                    foreach (var torrent in torrents.EnumerateArray())
                    {
                        var quality = torrent.GetProperty("quality").GetString() ?? "";
                        var torrentUrl = torrent.GetProperty("url").GetString() ?? "";
                        var size = torrent.TryGetProperty("size", out var s) ? s.GetString() ?? "" : "";

                        if (string.IsNullOrEmpty(torrentUrl)) continue;
                        var candidate = new YtsResult(title, year, quality, torrentUrl, size);

                        if (FileNameParser.IsQualityAcceptable(quality))
                        {
                            bestMatch = candidate;
                            break;
                        }

                        // keep the smallest above-standard release (1080p over 2160p) as the fallback
                        if (highMatch == null || Resolution(quality) < Resolution(highMatch.Quality))
                            highMatch = candidate;
                    }

                    if (bestMatch != null) break;
                }

                if (bestMatch == null && highMatch == null)
                {
                    logger.LogDebug("⚠️ No suitable match found for: {Name} ({Matches} candidates checked)", item.Name, matchCount);
                    continue;
                }

                foundCount++;
                var found = bestMatch ?? highMatch!;
                logger.LogInformation("✅ Found match for '{Name}': {Title} ({Year}) [{Quality}] - {Size}",
                    item.Name, found.Title, found.Year, found.Quality, found.Size);

                if (!item.Year.HasValue) item.Year = found.Year;

                // Acceptable quality is the standard the RSS monitor auto-downloads under — no prompt.
                if (bestMatch != null)
                {
                    await AutoDownloadAsync(item, bestMatch, $"✅ Auto-downloaded ({bestMatch.Quality})", ct);
                    continue;
                }

                item.HighQualityFirstSeen ??= DateTime.UtcNow;
                var waited = DateTime.UtcNow - item.HighQualityFirstSeen.Value;
                var waitHours = settings.CurrentValue.QualityWaitHours;
                var autoHours = settings.CurrentValue.QualityAutoDownloadHours;

                if (waited.TotalHours < waitHours)
                {
                    logger.LogInformation("⏳ Only {Quality} available for {Name}: {Elapsed:F1}h < {Wait}h — still looking for a better release",
                        highMatch!.Quality, item.Name, waited.TotalHours, waitHours);
                    db.Watchlist.Update(item);
                    continue;
                }

                // Nothing acceptable turned up in the whole window — take the high-quality file
                // rather than wait forever. 4K is still left to the prompt; we don't auto-pull those.
                if (waited.TotalHours >= autoHours && !FileNameParser.IsAbove1080p(highMatch!.Quality))
                {
                    await AutoDownloadAsync(item, highMatch,
                        $"⬇️ Auto-downloaded (no better quality after {autoHours}h) [{highMatch.Quality}]", ct);
                    continue;
                }

                item.Status = WatchlistStatus.AwaitingConfirmation;
                item.TorrentUrl = highMatch.TorrentUrl;
                item.Quality = highMatch.Quality;
                db.Watchlist.Update(item);

                var callbackId = Guid.NewGuid().ToString("N")[..8];
                var tcs = new TaskCompletionSource<string>();
                telegram.PendingCallbacks[callbackId] = tcs;

                var messageId = await telegram.SendInlineKeyboardAsync(
                    $"🎬 Found: {highMatch.Title} ({highMatch.Year})\n" +
                    $"Quality: {highMatch.Quality} | Size: {highMatch.Size}\n" +
                    $"Only above-standard releases after {waited.TotalHours:F0}h. Download?",
                    [
                        [
                            new InlineButton { Text = "✅ Download", CallbackData = $"{callbackId}:yes" },
                            new InlineButton { Text = "❌ Skip", CallbackData = $"{callbackId}:no" }
                        ]
                    ], ct);

                _ = HandleWatchlistCallbackAsync(item, highMatch, callbackId, tcs, messageId, ct);
            }
            catch (HttpRequestException hex)
            {
                logger.LogWarning(hex, "❌ HTTP error checking watchlist item: {Name}", item.Name);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "❌ Error checking watchlist item: {Name}", item.Name);
            }
        }

        logger.LogInformation("📊 Watchlist check summary: {Found} matches found out of {Total} items", foundCount, pending.Count);
    }

    /// <summary>YTS quality strings are "720p"/"1080p"/"2160p"/"3D". "3D" is not a resolution —
    /// hence DetectQuality's \d+p rather than leading digits, which read it as 3.</summary>
    public static int Resolution(string quality) =>
        int.TryParse(FileNameParser.DetectQuality(quality)?.TrimEnd('p'), out var r) ? r : int.MaxValue;

    private async Task AutoDownloadAsync(WatchlistItem item, YtsResult result, string note, CancellationToken ct)
    {
        item.TorrentUrl = result.TorrentUrl;
        item.Quality = result.Quality;

        if (!await transmission.AddTorrentAsync(result.TorrentUrl, ct))
        {
            logger.LogWarning("Failed to add watchlist torrent for {Name} [{Quality}]", item.Name, result.Quality);
            db.Watchlist.Update(item);
            return;
        }

        item.Status = WatchlistStatus.Downloading;
        db.Watchlist.Update(item);
        await telegram.SendMessageAsync($"{note}\n\n🎬 {result.Title} ({result.Year}) [{result.Quality}]", ct);
        state.AddActivity($"Watchlist download: {result.Title}");
        state.WatchlistCount = db.Watchlist.Count(w => w.Status == WatchlistStatus.Pending);
        state.NotifyChange();
    }

    private async Task HandleWatchlistCallbackAsync(
        WatchlistItem item, YtsResult result, string callbackId,
        TaskCompletionSource<string> tcs, int? messageId, CancellationToken ct)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromHours(24));
            var response = await tcs.Task.WaitAsync(cts.Token);
            telegram.PendingCallbacks.TryRemove(callbackId, out _);

            if (response == "yes")
            {
                var added = await transmission.AddTorrentAsync(item.TorrentUrl!, ct);
                if (added)
                {
                    item.Status = WatchlistStatus.Downloading;
                    db.Watchlist.Update(item);
                    if (messageId.HasValue)
                        await telegram.EditMessageAsync(messageId.Value, $"✅ Downloading\n\n🎬 {result.Title} ({result.Year}) [{result.Quality}]", ct);
                    else
                        await telegram.SendMessageAsync($"📥 Downloading: {result.Title} ({result.Year}) [{result.Quality}]", ct);
                    state.AddActivity($"Watchlist download: {result.Title}");
                    state.WatchlistCount = db.Watchlist.Count(w => w.Status == WatchlistStatus.Pending);
                    state.NotifyChange();
                }
            }
            else
            {
                item.Status = WatchlistStatus.Pending;
                item.TorrentUrl = null;
                item.Quality = null;
                // ponytail: restart the clock instead of cancelling — a skipped 1080p means "keep
                // looking", and without this the next cycle is already past the wait and re-asks.
                item.HighQualityFirstSeen = DateTime.UtcNow;
                db.Watchlist.Update(item);
                if (messageId.HasValue)
                    await telegram.EditMessageAsync(messageId.Value, $"⏭️ Skipped\n\n🎬 {result.Title} ({result.Year})", ct);
            }
        }
        catch
        {
            telegram.PendingCallbacks.TryRemove(callbackId, out _);
            item.Status = WatchlistStatus.Pending;
            db.Watchlist.Update(item);
        }
    }

    private record YtsResult(string Title, int Year, string Quality, string TorrentUrl, string Size);
}
