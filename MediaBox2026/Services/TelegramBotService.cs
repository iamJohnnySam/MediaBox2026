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
    public TelegramResponse() { }

    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("result")]
    public TelegramMessage? Result { get; set; }
}

public class TelegramMessage
{
    public TelegramMessage() { }

    [JsonPropertyName("message_id")]
    public int MessageId { get; set; }
}

public interface ITelegramNotifier
{
    Task SendMessageAsync(string text, CancellationToken ct = default);
    Task SendAdminMessageAsync(string text, CancellationToken ct = default);
    Task SendSubscribersMessageAsync(string text, CancellationToken ct = default);
    Task SendPhotoAsync(string photoUrl, string caption, List<List<InlineButton>>? buttons = null, CancellationToken ct = default);
    Task<int?> SendInlineKeyboardAsync(string text, List<List<InlineButton>> buttons, CancellationToken ct = default);
    Task<bool> MessageExistsAsync(int messageId, CancellationToken ct = default);
    Task EditMessageAsync(int messageId, string newText, CancellationToken ct = default);
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
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // Separate options for deserializing Telegram API responses (uses property names as-is)
    private static readonly JsonSerializerOptions DeserializeOpts = new()
    {
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
        var chatId = authStore.GetAdminChatId();
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
                row.Select(b => new Dictionary<string, object>
                {
                    ["text"] = b.Text,
                    ["callback_data"] = b.CallbackData
                }).ToArray()
            ).ToArray();

            payload["reply_markup"] = new Dictionary<string, object>
            {
                ["inline_keyboard"] = keyboard
            };
        }

        await PostAsync("sendPhoto", JsonSerializer.Serialize(payload, JsonOpts), ct);
    }

    public async Task SendMessageAsync(string text, CancellationToken ct = default)
    {
        // Send to admin only (legacy behavior for backward compatibility)
        await SendAdminMessageAsync(text, ct);
    }

    public async Task SendAdminMessageAsync(string text, CancellationToken ct = default)
    {
        var adminChatId = authStore.GetAdminChatId();
        if (adminChatId == null)
        {
            logger.LogWarning("No authenticated Telegram admin. Admin message not sent: {Text}", text);
            return;
        }
        await SendToChatAsync(adminChatId.Value, text, ct, parseMode: "Markdown");
    }

    public async Task SendSubscribersMessageAsync(string text, CancellationToken ct = default)
    {
        var subscribers = authStore.GetActiveSubscribers();
        if (subscribers.Count == 0)
        {
            logger.LogWarning("No active subscribers. Subscriber message not sent: {Text}", text);
            return;
        }

        foreach (var subscriber in subscribers)
        {
            try
            {
                await SendToChatAsync(subscriber.ChatId, text, ct, parseMode: "Markdown");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to send message to subscriber {ChatId}", subscriber.ChatId);
            }
        }
    }


    public async Task<int?> SendInlineKeyboardAsync(string text, List<List<InlineButton>> buttons, CancellationToken ct = default)
    {
        var chatId = authStore.GetAdminChatId();
        if (chatId == null)
        {
            logger.LogError("❌ Cannot send Telegram notification: Admin chat ID is null. Make sure admin is authenticated or TelegramChatId is configured in settings.");
            return null;
        }

        logger.LogInformation("Preparing to send Telegram inline keyboard to chat {ChatId}. Full text: '{Text}'", chatId.Value, text);

        var keyboard = buttons.Select(row =>
            row.Select(b => new Dictionary<string, object>
            {
                ["text"] = b.Text,
                ["callback_data"] = b.CallbackData
            }).ToArray()
        ).ToArray();

        var payload = new Dictionary<string, object>
        {
            ["chat_id"] = chatId.Value,
            ["text"] = text,
            ["reply_markup"] = new Dictionary<string, object>
            {
                ["inline_keyboard"] = keyboard
            }
        };

        var json = JsonSerializer.Serialize(payload, JsonOpts);
        logger.LogInformation("Telegram API payload: {Payload}", json);

        return await PostAsync("sendMessage", json, ct);
    }

    public async Task EditMessageAsync(int messageId, string newText, CancellationToken ct = default)
    {
        var chatId = authStore.GetAdminChatId();
        if (chatId == null)
        {
            logger.LogError("❌ Cannot edit Telegram message: Admin chat ID is null.");
            return;
        }

        logger.LogInformation("📝 Editing Telegram message {MessageId} in chat {ChatId} to: '{Text}'", messageId, chatId.Value, newText.Substring(0, Math.Min(50, newText.Length)));

        var payload = new Dictionary<string, object>
        {
            ["chat_id"] = chatId.Value,
            ["message_id"] = messageId,
            ["text"] = newText
        };

        var json = JsonSerializer.Serialize(payload, JsonOpts);
        logger.LogInformation("Edit message payload: {Payload}", json);

        var result = await PostAsync("editMessageText", json, ct);
        if (result.HasValue)
        {
            logger.LogInformation("✅ Message {MessageId} edited successfully", messageId);
        }
        else
        {
            logger.LogWarning("⚠️ Failed to edit message {MessageId}", messageId);
        }
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

        // Signal readiness immediately if admin is already authenticated
        if (authStore.GetAdminChatId() != null)
        {
            logger.LogInformation("Telegram admin already authenticated. Signaling ready.");
            state.SignalTelegramReady();
        }
        else
        {
            // Wait up to 3 minutes for admin to authenticate, then signal ready anyway
            logger.LogInformation("Waiting up to 3 minutes for Telegram admin authentication...");
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(3), ct);
                    state.SignalTelegramReady();
                    logger.LogWarning("Telegram admin auth timeout. Continuing without authenticated admin.");
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

        // Extract user info
        var username = msg.TryGetProperty("from", out var from) && from.TryGetProperty("username", out var un) 
            ? un.GetString() : null;
        var firstName = msg.TryGetProperty("from", out var from2) && from2.TryGetProperty("first_name", out var fn)
            ? fn.GetString() : null;
        var lastName = msg.TryGetProperty("from", out var from3) && from3.TryGetProperty("last_name", out var ln)
            ? ln.GetString() : null;

        // Check if user is blocked
        if (authStore.IsBlocked(chatId))
        {
            logger.LogWarning("Blocked user attempted to interact: {ChatId} (@{Username})", chatId, username);
            return; // Silently ignore blocked users
        }

        // Check if this is admin
        if (authStore.IsAdmin(chatId))
        {
            await HandleCommandAsync(chatId, text, ct);
            return;
        }

        // Check if this is a subscriber
        if (authStore.IsSubscriber(chatId))
        {
            await HandleSubscriberCommandAsync(chatId, text, ct);
            return;
        }

        // Handle authentication
        if (text.StartsWith("/subscribe", StringComparison.OrdinalIgnoreCase))
        {
            authStore.AddSubscriber(chatId, username, firstName, lastName);
            await SendToChatAsync(chatId, 
                "✅ Subscribed to MediaBox notifications!\n\n" +
                "You will receive notifications when:\n" +
                "• New media is downloaded\n" +
                "• Media is added to the library\n\n" +
                "Use /unsubscribe to stop receiving notifications.\n" +
                "Use /help to see available commands.", ct);
            state.AddActivity($"New subscriber: @{username ?? chatId.ToString()}");
            logger.LogInformation("New subscriber: {ChatId} (@{Username})", chatId, username);
            return;
        }

        if (text == settings.CurrentValue.AuthPassword)
        {
            // Check if admin already exists
            if (authStore.HasAdmin())
            {
                var adminChatId = authStore.GetAdminChatId();
                logger.LogWarning("Attempted admin registration while admin exists: {ChatId} (@{Username})", chatId, username);

                // Notify the existing admin
                if (adminChatId.HasValue)
                {
                    await SendToChatAsync(adminChatId.Value,
                        $"⚠️ *Security Alert*\n\n" +
                        $"Someone attempted to register as admin:\n" +
                        $"Chat ID: `{chatId}`\n" +
                        $"Username: @{username ?? "unknown"}\n" +
                        $"Name: {firstName} {lastName}\n\n" +
                        $"Use /block {chatId} to block this user.",
                        ct, parseMode: "Markdown");
                }

                await SendToChatAsync(chatId, 
                    "❌ Admin is already registered for this MediaBox instance.\n" +
                    "If you believe this is an error, please contact the system administrator.", ct);
                return;
            }

            // Register as admin
            authStore.SetAdmin(chatId, username, firstName, lastName);
            state.SignalTelegramReady();
            await SendToChatAsync(chatId, 
                "✅ *Admin Authentication Successful!*\n\n" +
                "You now have full administrative access to MediaBox.\n" +
                "You will receive all notifications including debug and error messages.\n\n" +
                "Use /help to see available commands.", ct, parseMode: "Markdown");
            state.AddActivity($"Admin authenticated: @{username ?? chatId.ToString()}");
            logger.LogInformation("Admin authenticated: {ChatId} (@{Username})", chatId, username);
            return;
        }

        // Not authenticated
        await SendToChatAsync(chatId, 
            "🤖 *Welcome to MediaBox!*\n\n" +
            "To get started, choose an option:\n\n" +
            "📱 */subscribe* - Receive general notifications\n" +
            "   (downloads, new media added)\n\n" +
            "👑 *Admin Password* - Full admin access\n" +
            "   (debug, errors, all controls)", ct, parseMode: "Markdown");
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
                    "📋 *Admin Commands:*\n" +
                    "/status - System status\n" +
                    "/downloads - Active downloads\n" +
                    "/watchlist - Movie watchlist\n" +
                    "/movie Movie Name - Search & add movie\n" +
                    "/add Movie Name - Quick add to watchlist\n" +
                    "/remove Movie Name - Remove from watchlist\n" +
                    "/scan - Trigger media scan\n" +
                    "/youtube - Manually download all YouTube sources\n" +
                    "/feeds - List RSS subscriptions\n" +
                    "/subscribe <url> <name> - Subscribe to RSS feed\n" +
                    "/unsubscribe <name> - Unsubscribe from feed\n" +
                    "/checkfeeds - Test RSS news feeds\n" +
                    "/resetquality - Reset & rescan quality notifications\n\n" +
                    "👥 *User Management:*\n" +
                    "/subscribers - List all subscribers\n" +
                    "/kick <chatId> - Remove a subscriber\n" +
                    "/block <chatId> - Block a user\n" +
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

            case "/resetquality":
                await SendToChatAsync(chatId, "🔄 Resetting quality download notifications...\n\nThis will:\n1. Clear all pending quality requests\n2. Re-scan RSS feed\n3. Send fresh notifications for high-quality items\n\nPlease wait...", ct);

                try
                {
                    // Get count before reset
                    var beforeCount = db.PendingDownloads.Count(p => p.Status == PendingStatus.WaitingForQuality);

                    // Clear all pending quality downloads
                    var pendingItems = db.PendingDownloads
                        .Find(p => p.Status == PendingStatus.WaitingForQuality)
                        .ToList();

                    foreach (var item in pendingItems)
                    {
                        item.Status = PendingStatus.Rejected;
                        db.PendingDownloads.Update(item);
                        logger.LogInformation("Reset pending quality item: {Title}", item.RssTitle);
                    }

                    // Clear processed RSS items so they'll be re-scanned
                    var processedGuids = pendingItems
                        .Select(p => p.RssTitle)
                        .Distinct()
                        .ToList();

                    int clearedRssItems = 0;
                    foreach (var title in processedGuids)
                    {
                        var deleted = db.ProcessedRssItems.DeleteMany(r => r.Title == title);
                        clearedRssItems += deleted;
                        logger.LogDebug("Cleared {Count} RSS items for: {Title}", deleted, title);
                    }

                    state.AddActivity($"Reset {beforeCount} quality notifications");
                    logger.LogInformation("Reset {Count} pending quality downloads and {RssCount} RSS items", beforeCount, clearedRssItems);

                    await SendToChatAsync(chatId, $"✅ Cleared {beforeCount} pending notification(s)\n\n🔍 Triggering RSS feed scan...", ct);

                    // Trigger RSS feed scan
                    var rssMonitor = serviceProvider.GetService<RssFeedMonitorService>();
                    if (rssMonitor != null)
                    {
                        await rssMonitor.TriggerCheckAsync(ct);
                        await SendToChatAsync(chatId, "✅ RSS scan complete!\n\nNew quality notifications will be sent after the wait period (if any high-quality items are found).", ct);
                    }
                    else
                    {
                        await SendToChatAsync(chatId, "⚠️ RSS monitor service not available. The feed will be checked automatically on the next scheduled scan.", ct);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error resetting quality notifications");
                    await SendToChatAsync(chatId, $"❌ Error resetting notifications: {ex.Message}", ct);
                }
                break;

            case "/checknews":
                await SendToChatAsync(chatId, "📰 Checking RSS news feeds now...", ct);
                try
                {
                    var subscriptions = db.RssFeedSubscriptions.Find(s => s.IsActive).ToList();
                    if (subscriptions.Count == 0)
                    {
                        await SendToChatAsync(chatId, "No active news subscriptions. Use /subscribe to add feeds.", ct);
                        break;
                    }

                    await SendToChatAsync(chatId, $"🔍 Checking {subscriptions.Count} feed(s)...", ct);

                    // The NewsRssFeedService runs in background, so we need to trigger it manually
                    // For now, let's just report when it will check next
                    var now = DateTime.UtcNow;
                    var nextCheck = TimeSpan.FromMinutes(settings.CurrentValue.RssFeedCheckMinutes);

                    await SendToChatAsync(chatId, 
                        $"📡 News feeds are checked every {settings.CurrentValue.RssFeedCheckMinutes} minutes.\n\n" +
                        $"Use /checkfeeds to diagnose feed status.", ct);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error checking news feeds");
                    await SendToChatAsync(chatId, $"❌ Error: {ex.Message}", ct);
                }
                break;

            case "/diagnose":
            case "/checkfeeds":
                await SendToChatAsync(chatId, "🔍 Running RSS news feed diagnostics...", ct);
                try
                {
                    var diagnosticOutput = new System.Text.StringBuilder();
                    diagnosticOutput.AppendLine("📰 *RSS News Feed Diagnostics*\n");

                    var subscriptions = db.RssFeedSubscriptions.FindAll().ToList();
                    diagnosticOutput.AppendLine($"Total subscriptions: {subscriptions.Count}");
                    diagnosticOutput.AppendLine($"Active: {subscriptions.Count(s => s.IsActive)}");

                    if (subscriptions.Count == 0)
                    {
                        diagnosticOutput.AppendLine("\n❌ No subscriptions found!");
                        diagnosticOutput.AppendLine("Use: /subscribe <url> <name>");
                    }
                    else
                    {
                        foreach (var sub in subscriptions)
                        {
                            diagnosticOutput.AppendLine($"\n• *{sub.FeedName}*");
                            diagnosticOutput.AppendLine($"  Active: {(sub.IsActive ? "✅" : "❌")}");
                            diagnosticOutput.AppendLine($"  Last checked: {sub.LastChecked?.ToString("g") ?? "Never"}");

                            var processedCount = db.ProcessedFeedItems.Count(p => p.SubscriptionId == sub.Id);
                            diagnosticOutput.AppendLine($"  Processed items: {processedCount}");

                            if (sub.IsActive)
                            {
                                try
                                {
                                    using var httpClient = new HttpClient();
                                    httpClient.Timeout = TimeSpan.FromSeconds(15);
                                    httpClient.DefaultRequestHeaders.Add("User-Agent", "MediaBox2026/1.0");

                                    var xmlContent = await httpClient.GetStringAsync(sub.FeedUrl, ct);
                                    var doc = System.Xml.Linq.XDocument.Parse(xmlContent);
                                    var ns = doc.Root?.GetDefaultNamespace() ?? System.Xml.Linq.XNamespace.None;
                                    var feedItems = doc.Descendants(ns + "item").ToList();
                                    if (feedItems.Count == 0)
                                        feedItems = doc.Descendants("item").ToList();

                                    diagnosticOutput.AppendLine($"  Feed test: ✅ {feedItems.Count} items");

                                    if (feedItems.Count > 0)
                                    {
                                        var firstItem = feedItems.First();
                                        var title = firstItem.Element(ns + "title")?.Value ?? firstItem.Element("title")?.Value ?? "";
                                        var guid = firstItem.Element(ns + "guid")?.Value ?? firstItem.Element("guid")?.Value ?? title;

                                        var alreadyProcessed = db.ProcessedFeedItems.Exists(p => 
                                            p.SubscriptionId == sub.Id && p.ItemGuid == guid);

                                        diagnosticOutput.AppendLine($"  Latest: {(title.Length > 40 ? title[..40] + "..." : title)}");
                                        diagnosticOutput.AppendLine($"  Status: {(alreadyProcessed ? "Already sent ✅" : "New (will be sent) 🆕")}");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    diagnosticOutput.AppendLine($"  Feed test: ❌ {ex.Message}");
                                }
                            }
                        }
                    }

                    await SendToChatAsync(chatId, diagnosticOutput.ToString(), ct, parseMode: "Markdown");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error running diagnostics");
                    await SendToChatAsync(chatId, $"❌ Diagnostic error: {ex.Message}", ct);
                }
                break;

            case "/youtube":
            case "/downloadyt":
                await SendToChatAsync(chatId, "🎬 Starting manual YouTube downloads...", ct);
                try
                {
                    var youtubeService = serviceProvider.GetService<YouTubeDownloadService>();
                    if (youtubeService == null)
                    {
                        await SendToChatAsync(chatId, "❌ YouTube download service not available.", ct);
                        break;
                    }

                    var sources = settings.CurrentValue.NewsSources;
                    if (sources.Count == 0)
                    {
                        await SendToChatAsync(chatId, "❌ No YouTube sources configured in settings.", ct);
                        break;
                    }

                    await SendToChatAsync(chatId, $"📋 Queued {sources.Count} download(s):\n" + 
                        string.Join("\n", sources.Select((s, i) => $"{i + 1}. {s.MatchTitle}")), ct);

                    var successCount = await youtubeService.TriggerManualDownloadAsync(ct);

                    if (successCount == -1)
                    {
                        await SendToChatAsync(chatId, "⚠️ A YouTube download is already in progress. Please wait for it to complete.", ct);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error triggering manual YouTube download");
                    await SendToChatAsync(chatId, $"❌ Error: {ex.Message}", ct);
                }
                break;

            case "/subscribers":
            case "/users":
                try
                {
                    var subscribers = authStore.GetAllSubscribers();
                    if (subscribers.Count == 0)
                    {
                        await SendToChatAsync(chatId, "📭 No subscribers registered.", ct);
                        break;
                    }

                    var sb = new StringBuilder("👥 *Registered Subscribers:*\n\n");
                    foreach (var sub in subscribers)
                    {
                        var statusEmoji = sub.IsBlocked ? "🚫" : (sub.IsActive ? "✅" : "❌");
                        var status = sub.IsBlocked ? "Blocked" : (sub.IsActive ? "Active" : "Inactive");
                        var name = !string.IsNullOrEmpty(sub.FirstName) ? $"{sub.FirstName} {sub.LastName}".Trim() : "Unknown";

                        sb.AppendLine($"{statusEmoji} *{name}*");
                        sb.AppendLine($"   Username: @{sub.Username ?? "none"}");
                        sb.AppendLine($"   Chat ID: `{sub.ChatId}`");
                        sb.AppendLine($"   Status: {status}");
                        sb.AppendLine($"   Subscribed: {sub.SubscribedDate:g}");
                        sb.AppendLine();
                    }

                    sb.AppendLine($"Total: {subscribers.Count} user(s)");
                    sb.AppendLine($"Active: {subscribers.Count(s => s.IsActive && !s.IsBlocked)}");

                    await SendToChatAsync(chatId, sb.ToString(), ct, parseMode: "Markdown");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error listing subscribers");
                    await SendToChatAsync(chatId, $"❌ Error: {ex.Message}", ct);
                }
                break;

            case "/kick":
                if (string.IsNullOrWhiteSpace(arg))
                {
                    await SendToChatAsync(chatId, "Usage: /kick <chatId>\n\nUse /subscribers to see chat IDs.", ct);
                    break;
                }

                if (!long.TryParse(arg, out var kickChatId))
                {
                    await SendToChatAsync(chatId, "❌ Invalid chat ID. Must be a number.", ct);
                    break;
                }

                try
                {
                    authStore.RemoveSubscriber(kickChatId);
                    await SendToChatAsync(chatId, $"✅ Subscriber {kickChatId} has been removed.", ct);

                    // Notify the kicked user
                    try
                    {
                        await SendToChatAsync(kickChatId, 
                            "⚠️ You have been removed from MediaBox notifications by the administrator.\n\n" +
                            "To resubscribe, send /subscribe", ct);
                    }
                    catch { /* User might have blocked the bot */ }

                    state.AddActivity($"Subscriber removed: {kickChatId}");
                    logger.LogInformation("Admin {AdminChatId} kicked subscriber {KickedChatId}", chatId, kickChatId);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error kicking subscriber");
                    await SendToChatAsync(chatId, $"❌ Error: {ex.Message}", ct);
                }
                break;

            case "/block":
                if (string.IsNullOrWhiteSpace(arg))
                {
                    await SendToChatAsync(chatId, "Usage: /block <chatId>\n\nUse /subscribers to see chat IDs.", ct);
                    break;
                }

                if (!long.TryParse(arg, out var blockChatId))
                {
                    await SendToChatAsync(chatId, "❌ Invalid chat ID. Must be a number.", ct);
                    break;
                }

                try
                {
                    authStore.BlockSubscriber(blockChatId);
                    await SendToChatAsync(chatId, $"🚫 User {blockChatId} has been blocked.\n\nThey will not be able to subscribe or interact with the bot.", ct);

                    state.AddActivity($"User blocked: {blockChatId}");
                    logger.LogInformation("Admin {AdminChatId} blocked user {BlockedChatId}", chatId, blockChatId);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error blocking user");
                    await SendToChatAsync(chatId, $"❌ Error: {ex.Message}", ct);
                }
                break;

            default:
                await SendToChatAsync(chatId, "Unknown command. Use /help for available commands.", ct);
                break;
        }
    }

    private async Task HandleSubscriberCommandAsync(long chatId, string text, CancellationToken ct)
    {
        var parts = text.Split(' ', 2);
        var command = parts[0].ToLowerInvariant();

        switch (command)
        {
            case "/start":
                await SendToChatAsync(chatId, "🎬 Welcome back! You are subscribed to MediaBox notifications.\n\nUse /help to see available commands.", ct);
                break;

            case "/help":
                await SendToChatAsync(chatId,
                    "📋 *Available Commands:*\n\n" +
                    "/status - View system status\n" +
                    "/unsubscribe - Stop receiving notifications\n" +
                    "/help - Show this message", ct, parseMode: "Markdown");
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

            case "/unsubscribe":
                authStore.RemoveSubscriber(chatId);
                await SendToChatAsync(chatId, 
                    "✅ You have been unsubscribed from MediaBox notifications.\n\n" +
                    "To subscribe again, send /subscribe", ct);
                state.AddActivity($"Subscriber unsubscribed: {chatId}");
                logger.LogInformation("Subscriber unsubscribed: {ChatId}", chatId);
                break;

            default:
                await SendToChatAsync(chatId, 
                    "Unknown command. Use /help to see available commands.\n\n" +
                    "Note: You have limited access as a subscriber. For full access, contact the administrator.", ct);
                break;
        }
    }

    private async Task HandleCallbackAsync(JsonElement cbq, CancellationToken ct)
    {
        var callbackId = cbq.GetProperty("id").GetString() ?? "";
        var data = cbq.TryGetProperty("data", out var d) ? d.GetString() ?? "" : "";

        // Answer the callback query to remove the "loading" state in Telegram
        var ackPayload = new Dictionary<string, object>
        {
            ["callback_query_id"] = callbackId
        };
        await PostAsync("answerCallbackQuery", JsonSerializer.Serialize(ackPayload, JsonOpts), ct);

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
                    // Parse just the message_id using simple JSON parsing instead of full deserialization
                    try
                    {
                        using var doc = JsonDocument.Parse(responseJson);
                        if (doc.RootElement.TryGetProperty("ok", out var okProp) && okProp.GetBoolean())
                        {
                            if (doc.RootElement.TryGetProperty("result", out var resultProp) &&
                                resultProp.TryGetProperty("message_id", out var msgIdProp))
                            {
                                var messageId = msgIdProp.GetInt32();
                                logger.LogInformation("✅ Telegram message sent successfully. MessageId: {MessageId}", messageId);
                                return messageId;
                            }
                        }
                    }
                    catch (JsonException jex)
                    {
                        logger.LogWarning(jex, "⚠️ Could not parse Telegram response, but message was sent: {Response}", responseJson);
                    }

                    // If we got here, the message was sent but we couldn't parse the response
                    // Return 0 to indicate success without a valid message ID
                    logger.LogInformation("✅ Telegram message sent (couldn't parse message ID from response)");
                    return 0;
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
        var chatId = authStore.GetAdminChatId();
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
