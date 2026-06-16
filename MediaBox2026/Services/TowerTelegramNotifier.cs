using System.Collections.Concurrent;
using Grpc.Net.Client;
using MediaBox2026.Models;
using Microsoft.Extensions.Options;
using Tower.Telegram.Grpc;

namespace MediaBox2026.Services;

/// <summary>
/// ITelegramNotifier implementation backed by the Tower gRPC bridge.
/// Activated when MediaBoxSettings.UseTowerTelegram is true (wired in Task 11).
/// Never throws on gRPC failure — logs a warning and returns a safe default so a
/// Tower outage cannot crash MediaBox services.
/// </summary>
public class TowerTelegramNotifier : ITelegramNotifier
{
    private readonly IOptionsMonitor<MediaBoxSettings> _settings;
    private readonly ILogger<TowerTelegramNotifier> _logger;

    // Lazy channel + client: built once on first use, keyed on TowerGrpcUrl so we
    // recreate when the URL changes between hot-reload cycles.
    private GrpcChannel? _channel;
    private TowerTelegram.TowerTelegramClient? _client;
    private string _channelUrl = "";
    private readonly object _clientLock = new();

    public ConcurrentDictionary<string, TaskCompletionSource<string>> PendingCallbacks { get; } = new();

    public TowerTelegramNotifier(
        IOptionsMonitor<MediaBoxSettings> settings,
        ILogger<TowerTelegramNotifier> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    // ── channel management ───────────────────────────────────────────────────

    private TowerTelegram.TowerTelegramClient GetClient()
    {
        var url = _settings.CurrentValue.TowerGrpcUrl;
        lock (_clientLock)
        {
            if (_client == null || _channelUrl != url)
            {
                _channel?.Dispose();
                _channel = GrpcChannel.ForAddress(url);
                _client = new TowerTelegram.TowerTelegramClient(_channel);
                _channelUrl = url;
                _logger.LogInformation("TowerTelegramNotifier: gRPC channel created to {Url}", url);
            }
            return _client;
        }
    }

    // ── helper: convert MediaBox InlineButton rows → proto ButtonRow list ───

    private static IEnumerable<ButtonRow> ToButtonRows(List<List<InlineButton>>? buttons)
    {
        if (buttons == null) yield break;
        foreach (var row in buttons)
        {
            var br = new ButtonRow();
            foreach (var b in row)
                br.Buttons.Add(new Button { Text = b.Text, CallbackData = b.CallbackData });
            yield return br;
        }
    }

    // ── ITelegramNotifier implementation ─────────────────────────────────────

    /// <summary>
    /// SendMessageAsync — original targets the admin chat (it calls SendAdminMessageAsync internally).
    /// Mapped to: Audience.Admin, ParseMode="Markdown".
    /// </summary>
    public async Task SendMessageAsync(string text, CancellationToken ct = default)
    {
        // Original: delegates to SendAdminMessageAsync → admin audience, Markdown.
        await SendAdminMessageAsync(text, ct);
    }

    /// <summary>
    /// SendAdminMessageAsync — targets admin chat, ParseMode="Markdown".
    /// Mapped to: Audience.Admin.
    /// </summary>
    public async Task SendAdminMessageAsync(string text, CancellationToken ct = default)
    {
        try
        {
            var req = new SendMessageRequest
            {
                Audience = Audience.Admin,
                Text = text,
                ParseMode = "Markdown"
            };
            await GetClient().SendMessageAsync(req, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TowerTelegramNotifier: gRPC SendAdminMessage failed (Tower may be offline). Message dropped: {Text}", text[..Math.Min(80, text.Length)]);
        }
    }

    /// <summary>
    /// SendSubscribersMessageAsync — fan-out to all active subscribers, ParseMode="Markdown".
    /// Mapped to: Audience.Subscribers.
    /// </summary>
    public async Task SendSubscribersMessageAsync(string text, CancellationToken ct = default)
    {
        try
        {
            var req = new SendMessageRequest
            {
                Audience = Audience.Subscribers,
                Text = text,
                ParseMode = "Markdown"
            };
            await GetClient().SendMessageAsync(req, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TowerTelegramNotifier: gRPC SendSubscribersMessage failed (Tower may be offline). Message dropped: {Text}", text[..Math.Min(80, text.Length)]);
        }
    }

    /// <summary>
    /// SendPhotoAsync — original targets admin chat (via authStore.GetAdminChatId()).
    /// Mapped to: Audience.Admin, ParseMode="Markdown".
    /// </summary>
    public async Task SendPhotoAsync(string photoUrl, string caption, List<List<InlineButton>>? buttons = null, CancellationToken ct = default)
    {
        try
        {
            var req = new SendPhotoRequest
            {
                Audience = Audience.Admin,
                PhotoUrl = photoUrl,
                Caption = caption,
                ParseMode = "Markdown"
            };
            req.Buttons.AddRange(ToButtonRows(buttons));
            await GetClient().SendPhotoAsync(req, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TowerTelegramNotifier: gRPC SendPhoto failed (Tower may be offline). Photo dropped: {Url}", photoUrl);
        }
    }

    /// <summary>
    /// SendInlineKeyboardAsync — original targets admin chat (via authStore.GetAdminChatId()),
    /// no explicit parse_mode set in the original but sends via sendMessage.
    /// Mapped to: InlineKeyboardRequest with ChatId=0 (Tower resolves admin), ParseMode="Markdown".
    /// Returns SendResult.MessageId (int?) or null on failure.
    /// </summary>
    public async Task<int?> SendInlineKeyboardAsync(string text, List<List<InlineButton>> buttons, CancellationToken ct = default)
    {
        try
        {
            var req = new InlineKeyboardRequest
            {
                // ChatId=0 signals Tower to use the admin chat id (same as Audience.Admin on SendMessage).
                // The InlineKeyboardRequest proto has no Audience field — Tower resolves ChatId=0 as admin.
                ChatId = 0,
                Text = text,
                ParseMode = "Markdown"
            };
            req.Buttons.AddRange(ToButtonRows(buttons));
            var result = await GetClient().SendInlineKeyboardAsync(req, cancellationToken: ct);
            return result.Ok ? result.MessageId : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TowerTelegramNotifier: gRPC SendInlineKeyboard failed (Tower may be offline). Text: {Text}", text[..Math.Min(80, text.Length)]);
            return null;
        }
    }

    /// <summary>
    /// MessageExistsAsync — original conservatively returns true after a best-effort check.
    /// We replicate that conservative behaviour: return true (assume the message exists) without
    /// making a gRPC call, since the Tower bridge has no "check message exists" RPC.
    /// </summary>
    public Task<bool> MessageExistsAsync(int messageId, CancellationToken ct = default)
    {
        // Original implementation always returns true (conservative approach).
        // Tower has no corresponding RPC; preserve the same safe default.
        return Task.FromResult(true);
    }

    /// <summary>
    /// EditMessageAsync — original targets admin chat (via authStore.GetAdminChatId()), no parse_mode.
    /// Mapped to: EditMessageRequest with ChatId=0 (Tower resolves admin).
    /// </summary>
    public async Task EditMessageAsync(int messageId, string newText, CancellationToken ct = default)
    {
        try
        {
            var req = new EditMessageRequest
            {
                // ChatId=0 signals Tower to use the admin chat id (Tower has the admin stored).
                ChatId = 0,
                MessageId = messageId,
                Text = newText
                // ParseMode not set in original editMessageText call; leave empty.
            };
            await GetClient().EditMessageAsync(req, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TowerTelegramNotifier: gRPC EditMessage failed (Tower may be offline). MessageId={MessageId}", messageId);
        }
    }
}
