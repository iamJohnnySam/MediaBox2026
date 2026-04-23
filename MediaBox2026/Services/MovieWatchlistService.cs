using System.Text.Json;
using MediaBox2026.Models;
using Microsoft.Extensions.Options;

namespace MediaBox2026.Services;

public class MovieWatchlistService(
    MediaDatabase db,
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
                logger.LogInformation("=== Watchlist Check Cycle Starting ===");
                if (_consecutiveFailures > 0)
                {
                    logger.LogWarning("⚠️ Consecutive failures: {Count}/{Max}", _consecutiveFailures, MaxConsecutiveFailures);
                }

                var checkStart = DateTime.UtcNow;
                await CheckWatchlistAsync(ct);
                _consecutiveFailures = 0; // Reset on success

                var duration = DateTime.UtcNow - checkStart;
                logger.LogInformation("✅ Watchlist check cycle completed in {Duration:F1}s", duration.TotalSeconds);
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

                YtsResult? bestMatch = null;
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

                        if (FileNameParser.IsQualityAcceptable(quality) && !string.IsNullOrEmpty(torrentUrl))
                        {
                            bestMatch = new YtsResult(title, year, quality, torrentUrl, size);
                            break;
                        }
                    }

                    if (bestMatch != null) break;
                }

                if (bestMatch == null)
                {
                    logger.LogDebug("⚠️ No suitable match found for: {Name} ({Matches} candidates checked)", item.Name, matchCount);
                    continue;
                }

                foundCount++;
                logger.LogInformation("✅ Found match for '{Name}': {Title} ({Year}) [{Quality}] - {Size}", 
                    item.Name, bestMatch.Title, bestMatch.Year, bestMatch.Quality, bestMatch.Size);

                item.Status = WatchlistStatus.AwaitingConfirmation;
                item.TorrentUrl = bestMatch.TorrentUrl;
                item.Quality = bestMatch.Quality;
                if (!item.Year.HasValue) item.Year = bestMatch.Year;
                db.Watchlist.Update(item);

                var callbackId = Guid.NewGuid().ToString("N")[..8];
                var tcs = new TaskCompletionSource<string>();
                telegram.PendingCallbacks[callbackId] = tcs;

                await telegram.SendInlineKeyboardAsync(
                    $"🎬 Found: {bestMatch.Title} ({bestMatch.Year})\n" +
                    $"Quality: {bestMatch.Quality} | Size: {bestMatch.Size}\n" +
                    $"Download?",
                    [
                        [
                            new InlineButton { Text = "✅ Download", CallbackData = $"{callbackId}:yes" },
                            new InlineButton { Text = "❌ Skip", CallbackData = $"{callbackId}:no" }
                        ]
                    ], ct);

                _ = HandleWatchlistCallbackAsync(item, bestMatch, callbackId, tcs, ct);
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

    private async Task HandleWatchlistCallbackAsync(
        WatchlistItem item, YtsResult result, string callbackId,
        TaskCompletionSource<string> tcs, CancellationToken ct)
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
                db.Watchlist.Update(item);
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
