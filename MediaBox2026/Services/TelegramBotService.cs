using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using MediaBox2026.Models;
using Microsoft.Extensions.Options;

namespace MediaBox2026.Services;

public class TelegramResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("result")]
    public TelegramMessage? Result { get; set; }
}

public class TelegramMessage
{
    [JsonPropertyName("message_id")]
    public int MessageId { get; set; }
}

public interface ITelegramNotifier
{
    Task SendMessageAsync(string text, CancellationToken ct = default);
    Task SendPhotoAsync(string photoUrl, string caption, List<List<InlineButton>>? buttons = null, CancellationToken ct = default);
    Task<int?> SendInlineKeyboardAsync(string text, List<List<InlineButton>> buttons, CancellationToken ct = default);
    Task<bool> MessageExistsAsync(int messageId, CancellationToken ct = default);
    ConcurrentDictionary<string, TaskCompletionSource<string>> PendingCallbacks { get; }
}

public class TelegramBotService(
    MediaDatabase db,
    IServiceProvider serviceProvider,
    TransmissionClient transmission,
    TelegramAuthStore authStore,
    MediaBoxState state,
    IOptionsMonitor<MediaBoxSettings> settings,
    ILogger<TelegramBotService> logger) : BackgroundService, ITelegramNotifier
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    private readonly HttpClient _http = new();
    private long _offset;

    public ConcurrentDictionary<string, TaskCompletionSource<string>> PendingCallbacks { get; } = new();
    private readonly ConcurrentDictionary<long, MovieSession> _movieSessions = new();

    private string ApiUrl => $"https://api.telegram.org/bot{settings.CurrentValue.TelegramBotToken}";

    public async Task SendPhotoAsync(string photoUrl, string caption, List<List<InlineButton>>? buttons = null, CancellationToken ct = default)
    {
        var chatId = authStore.GetChatId();
        if (chatId == null) return;

        var payload = new Dictionary<string, object>
        {
            ["chat_id"] = chatId.Value,
            ["photo"] = photoUrl,
            ["caption"] = caption
        };

        if (buttons != null)
        {
            var keyboard = buttons.Select(row =>
                row.Select(b => new { text = b.Text, callback_data = b.CallbackData }).ToArray()
            ).ToArray();
            payload["reply_markup"] = new { inline_keyboard = keyboard };
        }

        await PostAsync("sendPhoto", JsonSerializer.Serialize(payload, JsonOpts), ct);
    }

    public async Task SendMessageAsync(string text, CancellationToken ct = default)
    {
        var chatId = authStore.GetChatId();
        if (chatId == null)
        {
            logger.LogWarning("No authenticated Telegram chat. Message not sent: {Text}", text);
            return;
        }
        await SendToChatAsync(chatId.Value, text, ct, parseMode: "Markdown");
    }


    public async Task<int?> SendInlineKeyboardAsync(string text, List<List<InlineButton>> buttons, CancellationToken ct = default)
    {
        var chatId = authStore.GetChatId();
        if (chatId == null)
        {
            logger.LogError("❌ Cannot send Telegram notification: Chat ID is null. Make sure TelegramChatId is configured in settings.");
            return null;
        }

        logger.LogInformation("Preparing to send Telegram inline keyboard to chat {ChatId}: {Text}", chatId.Value, text.Substring(0, Math.Min(50, text.Length)));

        var keyboard = buttons.Select(row =>
            row.Select(b => new { text = b.Text, callback_data = b.CallbackData }).ToArray()
        ).ToArray();

        var payload = JsonSerializer.Serialize(new
        {
            chat_id = chatId.Value,
            text,
            reply_markup = new { inline_keyboard = keyboard }
        }, JsonOpts);

        logger.LogDebug("Telegram API payload: {Payload}", payload);

        return await PostAsync("sendMessage", payload, ct);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.CurrentValue.TelegramBotToken))
        {
            logger.LogWarning("⚠️ Telegram bot token not configured. Bot service disabled.");
            logger.LogWarning("To enable Telegram notifications, add TelegramBotToken to your settings.");
            state.SignalTelegramReady();
            return;
        }

        logger.LogInformation("🤖 Telegram bot service starting...");
        logger.LogInformation("Bot token configured: {TokenPrefix}***", settings.CurrentValue.TelegramBotToken.Substring(0, Math.Min(10, settings.CurrentValue.TelegramBotToken.Length)));

        await Task.Delay(2000, ct);

        // Signal readiness immediately if a chat is already authenticated
        if (authStore.GetChatId() != null)
        {
            logger.LogInformation("Telegram chat already authenticated. Signaling ready.");
            state.SignalTelegramReady();
        }
        else
        {
            // Wait up to 3 minutes for someone to authenticate, then signal ready anyway
            logger.LogInformation("Waiting up to 3 minutes for Telegram authentication...");
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(3), ct);
                    state.SignalTelegramReady();
                    logger.LogWarning("Telegram auth timeout. Continuing without authenticated chat.");
                }
                catch (OperationCanceledException) { state.SignalTelegramReady(); }
            }, ct);
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollUpdatesAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Telegram polling error");
                await Task.Delay(5000, ct);
            }
        }
    }

    private async Task PollUpdatesAsync(CancellationToken ct)
    {
        var url = $"{ApiUrl}/getUpdates?offset={_offset}&timeout=30";
        var response = await _http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) return;

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.GetProperty("ok").GetBoolean()) return;
        if (!root.TryGetProperty("result", out var results)) return;

        foreach (var update in results.EnumerateArray())
        {
            var updateId = update.GetProperty("update_id").GetInt64();
            _offset = updateId + 1;

            if (update.TryGetProperty("message", out var msg))
                await HandleMessageAsync(msg, ct);
            else if (update.TryGetProperty("callback_query", out var cbq))
                await HandleCallbackAsync(cbq, ct);
        }
    }

    private async Task HandleMessageAsync(JsonElement msg, CancellationToken ct)
    {
        if (!msg.TryGetProperty("chat", out var chat)) return;
        var chatId = chat.GetProperty("id").GetInt64();
        var text = msg.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";

        var authenticatedChat = authStore.GetChatId();

        if (authenticatedChat != chatId)
        {
            if (text == settings.CurrentValue.AuthPassword)
            {
                authStore.SetChatId(chatId);
                state.SignalTelegramReady();
                await SendToChatAsync(chatId, "✅ Authenticated! You will now receive MediaBox notifications.", ct);
                state.AddActivity("New Telegram chat authenticated");
                logger.LogInformation("Telegram chat {ChatId} authenticated", chatId);
            }
            else
            {
                await SendToChatAsync(chatId, "🔒 Please enter your MediaBox password to authenticate.", ct);
            }
            return;
        }

        await HandleCommandAsync(chatId, text, ct);
    }

    private async Task HandleCommandAsync(long chatId, string text, CancellationToken ct)
    {
        var parts = text.Split(' ', 2);
        var command = parts[0].ToLowerInvariant();
        var arg = parts.Length > 1 ? parts[1].Trim() : "";

        switch (command)
        {
            case "/start":
                await SendToChatAsync(chatId, "🎬 MediaBox is running! Use /help for commands.", ct);
                break;

            case "/help":
                await SendToChatAsync(chatId,
                    "📋 *Commands:*\n" +
                    "/status - System status\n" +
                    "/downloads - Active downloads\n" +
                    "/watchlist - Movie watchlist\n" +
                    "/movie Movie Name - Search & add movie\n" +
                    "/add Movie Name - Quick add to watchlist\n" +
                    "/remove Movie Name - Remove from watchlist\n" +
                    "/scan - Trigger media scan\n" +
                    "/feeds - List RSS subscriptions\n" +
                    "/subscribe <url> <name> - Subscribe to RSS feed\n" +
                    "/unsubscribe <name> - Unsubscribe from feed\n" +
                    "/help - Show this message",
                    ct, parseMode: "Markdown");
                break;

            case "/status":
                var statusMsg = $"📊 *MediaBox Status*\n" +
                    $"📺 TV Shows: {state.TvShowCount}\n" +
                    $"🎬 Movies: {state.MovieCount}\n" +
                    $"📥 Active Downloads: {state.ActiveDownloads}\n" +
                    $"📋 Watchlist: {state.WatchlistCount}\n" +
                    $"📹 YouTube: {state.YouTubeCount}\n" +
                    $"🔍 Last Scan: {state.LastMediaScan:g}\n" +
                    $"📡 Last RSS: {state.LastRssCheck:g}";
                await SendToChatAsync(chatId, statusMsg, ct, parseMode: "Markdown");
                break;

            case "/downloads":
                var torrents = await transmission.GetTorrentsAsync(ct);
                if (torrents.Count == 0)
                {
                    await SendToChatAsync(chatId, "No active downloads.", ct);
                }
                else
                {
                    var sb = new StringBuilder("📥 *Downloads:*\n");
                    foreach (var tor in torrents)
                        sb.AppendLine($"• {tor.Name} - {tor.PercentDone:P0} ({tor.StatusText})");
                    await SendToChatAsync(chatId, sb.ToString(), ct, parseMode: "Markdown");
                }
                break;

            case "/watchlist":
                var items = db.Watchlist.FindAll()
                    .Where(w => w.Status is WatchlistStatus.Pending or WatchlistStatus.AwaitingConfirmation)
                    .ToList();
                if (items.Count == 0)
                {
                    await SendToChatAsync(chatId, "Watchlist is empty.", ct);
                }
                else
                {
                    var sb = new StringBuilder("📋 *Watchlist:*\n");
                    foreach (var item in items)
                    {
                        var yearStr = item.Year.HasValue ? $" ({item.Year})" : "";
                        sb.AppendLine($"• {item.Name}{yearStr} - {item.Status}");
                    }
                    await SendToChatAsync(chatId, sb.ToString(), ct, parseMode: "Markdown");
                }
                break;

            case "/movie":
            case "/search":
                await HandleMovieSearchAsync(chatId, arg, ct);
                break;

            case "/add":
                if (string.IsNullOrWhiteSpace(arg))
                {
                    await SendToChatAsync(chatId, "Usage: /add Movie Name", ct);
                    break;
                }
                var parsed = FileNameParser.Parse(arg);
                db.Watchlist.Insert(new WatchlistItem
                {
                    Name = parsed.CleanName.Length > 0 ? parsed.CleanName : arg,
                    Year = parsed.Year,
                    Status = WatchlistStatus.Pending,
                    AddedDate = DateTime.UtcNow
                });
                state.WatchlistCount = db.Watchlist.Count(w => w.Status == WatchlistStatus.Pending);
                state.AddActivity($"Added to watchlist: {arg}");
                await SendToChatAsync(chatId, $"✅ Added to watchlist: {arg}", ct);
                break;

            case "/remove":
                if (string.IsNullOrWhiteSpace(arg))
                {
                    await SendToChatAsync(chatId, "Usage: /remove Movie Name", ct);
                    break;
                }
                var toRemove = db.Watchlist.FindAll()
                    .FirstOrDefault(w => w.Name.Contains(arg, StringComparison.OrdinalIgnoreCase));
                if (toRemove != null)
                {
                    toRemove.Status = WatchlistStatus.Cancelled;
                    db.Watchlist.Update(toRemove);
                    state.WatchlistCount = db.Watchlist.Count(w => w.Status == WatchlistStatus.Pending);
                    await SendToChatAsync(chatId, $"✅ Removed from watchlist: {toRemove.Name}", ct);
                }
                else
                {
                    await SendToChatAsync(chatId, $"❌ Not found in watchlist: {arg}", ct);
                }
                break;

            case "/scan":
                await SendToChatAsync(chatId, "🔍 Starting media scan...", ct);
                var catalog = serviceProvider.GetRequiredService<MediaCatalogService>();
                await catalog.ScanAllAsync(ct);
                await SendToChatAsync(chatId, $"✅ Scan complete. TV: {state.TvShowCount}, Movies: {state.MovieCount}", ct);
                break;

            case "/feeds":
                var feeds = db.RssFeedSubscriptions.Find(f => f.IsActive).ToList();
                if (feeds.Count == 0)
                {
                    await SendToChatAsync(chatId, "No RSS feeds subscribed.", ct);
                }
                else
                {
                    var sb = new StringBuilder("📰 *RSS Subscriptions:*\n\n");
                    foreach (var feed in feeds)
                    {
                        var lastChecked = feed.LastChecked.HasValue
                            ? feed.LastChecked.Value.ToString("g")
                            : "Never";
                        sb.AppendLine($"• *{feed.FeedName}*");
                        sb.AppendLine($"  URL: {feed.FeedUrl}");
                        sb.AppendLine($"  Last checked: {lastChecked}");
                        sb.AppendLine();
                    }
                    await SendToChatAsync(chatId, sb.ToString(), ct, parseMode: "Markdown");
                }
                break;

            case "/subscribe":
                if (string.IsNullOrWhiteSpace(arg))
                {
                    await SendToChatAsync(chatId, "Usage: /subscribe <url> <name>\n\nExample:\n/subscribe https://example.com/feed.xml My News Feed", ct);
                    break;
                }
                var subscribeParts = arg.Split(' ', 2);
                if (subscribeParts.Length < 2)
                {
                    await SendToChatAsync(chatId, "Please provide both URL and name.\n\nUsage: /subscribe <url> <name>", ct);
                    break;
                }
                var subscribeUrl = subscribeParts[0].Trim();
                var subscribeName = subscribeParts[1].Trim();

                if (!Uri.TryCreate(subscribeUrl, UriKind.Absolute, out _))
                {
                    await SendToChatAsync(chatId, "❌ Invalid URL format.", ct);
                    break;
                }

                var existingFeed = db.RssFeedSubscriptions.FindOne(f => f.FeedUrl == subscribeUrl);
                if (existingFeed != null)
                {
                    if (existingFeed.IsActive)
                    {
                        await SendToChatAsync(chatId, $"ℹ️ Already subscribed to this feed as '{existingFeed.FeedName}'.", ct);
                    }
                    else
                    {
                        existingFeed.IsActive = true;
                        existingFeed.FeedName = subscribeName;
                        db.RssFeedSubscriptions.Update(existingFeed);
                        await SendToChatAsync(chatId, $"✅ Re-activated subscription: {subscribeName}", ct);
                        state.AddActivity($"RSS feed re-activated: {subscribeName}");
                    }
                    break;
                }

                db.RssFeedSubscriptions.Insert(new RssFeedSubscription
                {
                    FeedUrl = subscribeUrl,
                    FeedName = subscribeName,
                    SubscribedDate = DateTime.UtcNow,
                    IsActive = true
                });
                await SendToChatAsync(chatId, $"✅ Subscribed to: {subscribeName}\n\nYou'll receive notifications when new items are published.", ct);
                state.AddActivity($"RSS feed subscribed: {subscribeName}");
                logger.LogInformation("Subscribed to RSS feed: {Name} - {Url}", subscribeName, subscribeUrl);
                break;

            case "/unsubscribe":
                if (string.IsNullOrWhiteSpace(arg))
                {
                    await SendToChatAsync(chatId, "Usage: /unsubscribe <name>\n\nUse /feeds to see your subscriptions.", ct);
                    break;
                }
                var feedToRemove = db.RssFeedSubscriptions.FindOne(f =>
                    f.IsActive && f.FeedName.Contains(arg, StringComparison.OrdinalIgnoreCase));
                if (feedToRemove != null)
                {
                    feedToRemove.IsActive = false;
                    db.RssFeedSubscriptions.Update(feedToRemove);
                    await SendToChatAsync(chatId, $"✅ Unsubscribed from: {feedToRemove.FeedName}", ct);
                    state.AddActivity($"RSS feed unsubscribed: {feedToRemove.FeedName}");
                    logger.LogInformation("Unsubscribed from RSS feed: {Name}", feedToRemove.FeedName);
                }
                else
                {
                    await SendToChatAsync(chatId, $"❌ Feed not found: {arg}\n\nUse /feeds to see your subscriptions.", ct);
                }
                break;

            default:
                await SendToChatAsync(chatId, "Unknown command. Use /help for available commands.", ct);
                break;
        }
    }

    private async Task HandleCallbackAsync(JsonElement cbq, CancellationToken ct)
    {
        var callbackId = cbq.GetProperty("id").GetString() ?? "";
        var data = cbq.TryGetProperty("data", out var d) ? d.GetString() ?? "" : "";

        await PostAsync("answerCallbackQuery", JsonSerializer.Serialize(new { callback_query_id = callbackId }, JsonOpts), ct);

        // Movie search callbacks
        if (data.StartsWith("ms:"))
        {
            var chatId = cbq.GetProperty("message").GetProperty("chat").GetProperty("id").GetInt64();
            var action = data[3..];
            await HandleMovieSessionCallbackAsync(chatId, action, ct);
            return;
        }

        var colonIdx = data.IndexOf(':');
        if (colonIdx > 0)
        {
            var prefix = data[..colonIdx];
            var value = data[(colonIdx + 1)..];

            if (PendingCallbacks.TryGetValue(prefix, out var tcs))
            {
                tcs.TrySetResult(value);
            }
        }
    }

    private async Task SendToChatAsync(long chatId, string text, CancellationToken ct, string? parseMode = null)
    {
        var payload = new Dictionary<string, object>
        {
            ["chat_id"] = chatId,
            ["text"] = text
        };
        if (parseMode != null) payload["parse_mode"] = parseMode;

        await PostAsync("sendMessage", JsonSerializer.Serialize(payload, JsonOpts), ct);
    }

    private async Task<int?> PostAsync(string method, string json, CancellationToken ct)
    {
        const int maxRetries = 3;
        const int retryDelayMs = 1000;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                logger.LogInformation("Calling Telegram API: {Method} (attempt {Attempt}/{MaxRetries})", method, attempt, maxRetries);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await _http.PostAsync($"{ApiUrl}/{method}", content, ct);

                var responseJson = await response.Content.ReadAsStringAsync(ct);
                logger.LogInformation("Telegram API response ({StatusCode}): {Response}", response.StatusCode, responseJson);

                if (response.IsSuccessStatusCode)
                {
                    var result = JsonSerializer.Deserialize<TelegramResponse>(responseJson, JsonOpts);
                    if (result?.Ok == true && result.Result != null)
                    {
                        logger.LogInformation("✅ Telegram message sent successfully. MessageId: {MessageId}", result.Result.MessageId);
                        return result.Result.MessageId;
                    }
                    else
                    {
                        logger.LogWarning("⚠️ Telegram API returned success but response structure unexpected: {Response}", responseJson);
                        return null;
                    }
                }
                else if ((int)response.StatusCode == 429) // Rate limited
                {
                    logger.LogWarning("⚠️ Telegram API rate limit hit. Waiting before retry...");
                    await Task.Delay(retryDelayMs * attempt, ct);
                    continue;
                }
                else
                {
                    logger.LogError("❌ Telegram API call failed with status {StatusCode}: {Response}", response.StatusCode, responseJson);

                    // Don't retry on client errors (4xx except 429)
                    if ((int)response.StatusCode >= 400 && (int)response.StatusCode < 500)
                    {
                        logger.LogError("Client error detected, not retrying.");
                        return null;
                    }

                    if (attempt < maxRetries)
                    {
                        logger.LogInformation("Retrying in {Delay}ms...", retryDelayMs * attempt);
                        await Task.Delay(retryDelayMs * attempt, ct);
                    }
                }
            }
            catch (HttpRequestException hex)
            {
                logger.LogError(hex, "❌ HTTP error during Telegram API call to {Method} (attempt {Attempt}/{MaxRetries})", method, attempt, maxRetries);

                if (attempt < maxRetries)
                {
                    logger.LogInformation("Retrying in {Delay}ms...", retryDelayMs * attempt);
                    await Task.Delay(retryDelayMs * attempt, ct);
                }
            }
            catch (TaskCanceledException) when (ct.IsCancellationRequested)
            {
                logger.LogWarning("Telegram API call cancelled");
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "❌ Exception during Telegram API call to {Method} (attempt {Attempt}/{MaxRetries})", method, attempt, maxRetries);

                if (attempt < maxRetries)
                {
                    logger.LogInformation("Retrying in {Delay}ms...", retryDelayMs * attempt);
                    await Task.Delay(retryDelayMs * attempt, ct);
                }
            }
        }

        logger.LogError("❌ All {MaxRetries} attempts to call Telegram API failed", maxRetries);
        return null;
    }

    public async Task<bool> MessageExistsAsync(int messageId, CancellationToken ct = default)
    {
        var chatId = authStore.GetChatId();
        if (chatId == null) return false;

        try
        {
            // Try to get the message - if it exists, this will succeed
            var url = $"{ApiUrl}/getUpdates";
            var response = await _http.GetAsync(url, ct);

            // Note: Telegram doesn't have a direct "check if message exists" API
            // The message might have been deleted if user cleared chat
            // We'll assume it doesn't exist if we haven't received a callback after 24h
            return true; // Conservative approach - assume it exists
        }
        catch
        {
            return false;
        }
    }

    private async Task HandleMovieSearchAsync(long chatId, string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            await SendToChatAsync(chatId, "Usage: /movie Movie Name", ct);
            return;
        }

        await SendToChatAsync(chatId, $"🔍 Searching for \"{query}\"...", ct);

        try
        {
            using var http = new HttpClient();
            var url = $"https://yts.bz/api/v2/list_movies.json?query_term={Uri.EscapeDataString(query)}&limit=10&sort_by=rating";
            var response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                await SendToChatAsync(chatId, "⚠️ Movie search is temporarily unavailable. Please try again later.", ct);
                return;
            }

            var jsonText = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(jsonText);
            var json = doc.RootElement;
            if (!json.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("movies", out var movies) ||
                movies.ValueKind != JsonValueKind.Array || movies.GetArrayLength() == 0)
            {
                await SendToChatAsync(chatId, $"❌ No movies found for \"{query}\". Try a different search term.", ct);
                return;
            }

            var results = new List<MovieSearchResult>();
            foreach (var m in movies.EnumerateArray())
            {
                var torrents = new List<(string Quality, string Url, string Size)>();
                if (m.TryGetProperty("torrents", out var tArr))
                {
                    foreach (var t in tArr.EnumerateArray())
                    {
                        var tq = t.GetProperty("quality").GetString() ?? "";
                        var tu = t.GetProperty("url").GetString() ?? "";
                        var ts = t.TryGetProperty("size", out var sz) ? sz.GetString() ?? "" : "";
                        if (!string.IsNullOrEmpty(tu))
                            torrents.Add((tq, tu, ts));
                    }
                }

                results.Add(new MovieSearchResult(
                    Title: m.GetProperty("title").GetString() ?? "",
                    Year: m.GetProperty("year").GetInt32(),
                    Rating: m.TryGetProperty("rating", out var r) ? r.GetDouble() : 0,
                    ImdbCode: m.TryGetProperty("imdb_code", out var ic) ? ic.GetString() : null,
                    PosterUrl: m.TryGetProperty("medium_cover_image", out var p) ? p.GetString() : null,
                    TrailerCode: m.TryGetProperty("yt_trailer_code", out var tr) ? tr.GetString() : null,
                    Genres: m.TryGetProperty("genres", out var g)
                        ? string.Join(", ", g.EnumerateArray().Select(x => x.GetString()))
                        : null,
                    Torrents: torrents
                ));
            }

            var session = new MovieSession { Results = results, CurrentIndex = 0 };
            _movieSessions[chatId] = session;
            await SendMovieResultAsync(chatId, session, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Movie search failed for: {Query}", query);
            await SendToChatAsync(chatId, "⚠️ Movie search failed. Please try again later.", ct);
        }
    }

    private async Task SendMovieResultAsync(long chatId, MovieSession session, CancellationToken ct)
    {
        var movie = session.Results[session.CurrentIndex];
        var caption = new StringBuilder();
        caption.AppendLine($"🎬 {movie.Title} ({movie.Year})");
        caption.AppendLine($"⭐ Rating: {movie.Rating:F1}/10");
        if (!string.IsNullOrEmpty(movie.Genres))
            caption.AppendLine($"🎭 {movie.Genres}");
        if (!string.IsNullOrEmpty(movie.TrailerCode))
            caption.AppendLine($"🎥 Trailer: https://youtube.com/watch?v={movie.TrailerCode}");
        if (movie.Torrents.Count > 0)
            caption.AppendLine($"📀 {string.Join(" | ", movie.Torrents.Select(t => $"{t.Quality} ({t.Size})"))}");
        else
            caption.AppendLine("📀 No torrents available yet");
        caption.Append($"\nResult {session.CurrentIndex + 1} of {session.Results.Count}");

        var buttons = new List<List<InlineButton>>();
        var navRow = new List<InlineButton>();
        if (session.CurrentIndex > 0)
            navRow.Add(new InlineButton { Text = "⬅️ Prev", CallbackData = "ms:prev" });
        navRow.Add(new InlineButton { Text = "✅ Add to Watchlist", CallbackData = "ms:add" });
        if (session.CurrentIndex < session.Results.Count - 1)
            navRow.Add(new InlineButton { Text = "➡️ Next", CallbackData = "ms:next" });
        buttons.Add(navRow);
        buttons.Add([new InlineButton { Text = "❌ Cancel", CallbackData = "ms:cancel" }]);

        if (!string.IsNullOrEmpty(movie.PosterUrl))
        {
            await SendPhotoToChatAsync(chatId, movie.PosterUrl, caption.ToString(), buttons, ct);
        }
        else
        {
            await SendInlineKeyboardToChatAsync(chatId, caption.ToString(), buttons, ct);
        }
    }

    private async Task HandleMovieSessionCallbackAsync(long chatId, string action, CancellationToken ct)
    {
        if (!_movieSessions.TryGetValue(chatId, out var session))
        {
            await SendToChatAsync(chatId, "No active movie search. Use /movie to start one.", ct);
            return;
        }

        switch (action)
        {
            case "next":
                if (session.CurrentIndex < session.Results.Count - 1)
                {
                    session.CurrentIndex++;
                    await SendMovieResultAsync(chatId, session, ct);
                }
                break;

            case "prev":
                if (session.CurrentIndex > 0)
                {
                    session.CurrentIndex--;
                    await SendMovieResultAsync(chatId, session, ct);
                }
                break;

            case "add":
                var movie = session.Results[session.CurrentIndex];
                db.Watchlist.Insert(new WatchlistItem
                {
                    Name = movie.Title,
                    Year = movie.Year,
                    ImdbCode = movie.ImdbCode,
                    PosterUrl = movie.PosterUrl,
                    TrailerCode = movie.TrailerCode,
                    Status = WatchlistStatus.Pending,
                    AddedDate = DateTime.UtcNow
                });
                state.WatchlistCount = db.Watchlist.Count(w => w.Status == WatchlistStatus.Pending);
                state.AddActivity($"Added to watchlist: {movie.Title} ({movie.Year})");
                state.NotifyChange();
                _movieSessions.TryRemove(chatId, out _);
                await SendToChatAsync(chatId, $"✅ Added \"{movie.Title} ({movie.Year})\" to watchlist!", ct);
                break;

            case "cancel":
                _movieSessions.TryRemove(chatId, out _);
                await SendToChatAsync(chatId, "🚫 Movie search cancelled.", ct);
                break;
        }
    }

    private async Task SendPhotoToChatAsync(long chatId, string photoUrl, string caption, List<List<InlineButton>> buttons, CancellationToken ct)
    {
        var keyboard = buttons.Select(row =>
            row.Select(b => new { text = b.Text, callback_data = b.CallbackData }).ToArray()
        ).ToArray();

        var payload = JsonSerializer.Serialize(new
        {
            chat_id = chatId,
            photo = photoUrl,
            caption,
            reply_markup = new { inline_keyboard = keyboard }
        }, JsonOpts);

        await PostAsync("sendPhoto", payload, ct);
    }

    private async Task SendInlineKeyboardToChatAsync(long chatId, string text, List<List<InlineButton>> buttons, CancellationToken ct)
    {
        var keyboard = buttons.Select(row =>
            row.Select(b => new { text = b.Text, callback_data = b.CallbackData }).ToArray()
        ).ToArray();

        var payload = JsonSerializer.Serialize(new
        {
            chat_id = chatId,
            text,
            reply_markup = new { inline_keyboard = keyboard }
        }, JsonOpts);

        await PostAsync("sendMessage", payload, ct);
    }

    private record MovieSearchResult(
        string Title, int Year, double Rating, string? ImdbCode,
        string? PosterUrl, string? TrailerCode, string? Genres,
        List<(string Quality, string Url, string Size)> Torrents);

    private class MovieSession
    {
        public List<MovieSearchResult> Results { get; set; } = [];
        public int CurrentIndex { get; set; }
    }

    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }
}
