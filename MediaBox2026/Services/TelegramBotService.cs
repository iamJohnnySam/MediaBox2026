using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using MediaBox2026.Models;
using Microsoft.Extensions.Options;

namespace MediaBox2026.Services;

public interface ITelegramNotifier
{
    Task SendMessageAsync(string text, CancellationToken ct = default);
    Task SendInlineKeyboardAsync(string text, List<List<InlineButton>> buttons, CancellationToken ct = default);
    ConcurrentDictionary<string, TaskCompletionSource<string>> PendingCallbacks { get; }
}

public class TelegramBotService(
    MediaDatabase db,
    MediaCatalogService catalog,
    TransmissionClient transmission,
    MediaBoxState state,
    IOptionsMonitor<MediaBoxSettings> settings,
    ILogger<TelegramBotService> logger) : BackgroundService, ITelegramNotifier
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http = new();
    private long _offset;

    public ConcurrentDictionary<string, TaskCompletionSource<string>> PendingCallbacks { get; } = new();

    private string ApiUrl => $"https://api.telegram.org/bot{settings.CurrentValue.TelegramBotToken}";

    public async Task SendMessageAsync(string text, CancellationToken ct = default)
    {
        var chatId = db.GetAuthenticatedChatId();
        if (chatId == null)
        {
            logger.LogWarning("No authenticated Telegram chat. Message not sent: {Text}", text);
            return;
        }
        await SendToChatAsync(chatId.Value, text, ct);
    }

    public async Task SendInlineKeyboardAsync(string text, List<List<InlineButton>> buttons, CancellationToken ct = default)
    {
        var chatId = db.GetAuthenticatedChatId();
        if (chatId == null) return;

        var keyboard = buttons.Select(row =>
            row.Select(b => new { text = b.Text, callback_data = b.CallbackData }).ToArray()
        ).ToArray();

        var payload = JsonSerializer.Serialize(new
        {
            chat_id = chatId.Value,
            text,
            reply_markup = new { inline_keyboard = keyboard }
        });

        await PostAsync("sendMessage", payload, ct);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.CurrentValue.TelegramBotToken))
        {
            logger.LogWarning("Telegram bot token not configured. Bot service disabled.");
            return;
        }

        logger.LogInformation("Telegram bot service starting...");
        await Task.Delay(2000, ct);

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

        var authenticatedChat = db.GetAuthenticatedChatId();

        if (authenticatedChat != chatId)
        {
            if (text == settings.CurrentValue.AuthPassword)
            {
                db.SetAuthenticatedChat(chatId);
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
                    "/add Movie Name - Add to watchlist\n" +
                    "/remove Movie Name - Remove from watchlist\n" +
                    "/scan - Trigger media scan\n" +
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
                await catalog.ScanAllAsync(ct);
                await SendToChatAsync(chatId, $"✅ Scan complete. TV: {state.TvShowCount}, Movies: {state.MovieCount}", ct);
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

        await PostAsync("answerCallbackQuery", JsonSerializer.Serialize(new { callback_query_id = callbackId }), ct);

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

        await PostAsync("sendMessage", JsonSerializer.Serialize(payload), ct);
    }

    private async Task PostAsync(string method, string json, CancellationToken ct)
    {
        try
        {
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            await _http.PostAsync($"{ApiUrl}/{method}", content, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Telegram API call failed: {Method}", method);
        }
    }
}
