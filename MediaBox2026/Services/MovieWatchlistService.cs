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
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("Movie watchlist waiting for Telegram readiness...");
        await state.WaitForTelegramReadyAsync(ct);
        await Task.Delay(TimeSpan.FromMinutes(2), ct);
        logger.LogInformation("Movie watchlist service started");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CheckWatchlistAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Watchlist check error");
            }

            await Task.Delay(TimeSpan.FromHours(settings.CurrentValue.WatchlistCheckHours), ct);
        }
    }

    private async Task CheckWatchlistAsync(CancellationToken ct)
    {
        var pending = db.Watchlist
            .Find(w => w.Status == WatchlistStatus.Pending)
            .ToList();

        if (pending.Count == 0) return;

        using var http = httpFactory.CreateClient();

        foreach (var item in pending)
        {
            try
            {
                var query = item.Year.HasValue ? $"{item.Name} {item.Year}" : item.Name;
                var url = $"https://yts.bz/api/v2/list_movies.json?query_term={Uri.EscapeDataString(query)}&limit=5";
                var response = await http.GetAsync(url, ct);
                if (!response.IsSuccessStatusCode) continue;

                var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
                if (!json.TryGetProperty("data", out var data) ||
                    !data.TryGetProperty("movies", out var movies) ||
                    movies.GetArrayLength() == 0) continue;

                YtsResult? bestMatch = null;
                foreach (var movie in movies.EnumerateArray())
                {
                    var title = movie.GetProperty("title").GetString() ?? "";
                    var year = movie.GetProperty("year").GetInt32();
                    var score = FileNameParser.FuzzyMatch(title, item.Name);

                    if (score < 0.5) continue;
                    if (item.Year.HasValue && year != item.Year.Value) continue;

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

                if (bestMatch == null) continue;

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
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error checking watchlist item: {Name}", item.Name);
            }
        }
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
