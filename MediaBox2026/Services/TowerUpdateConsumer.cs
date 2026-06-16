using Grpc.Core;
using Grpc.Net.Client;
using MediaBox2026.Models;
using Microsoft.Extensions.Options;
using Tower.Telegram.Grpc;

namespace MediaBox2026.Services;

/// <summary>
/// BackgroundService that streams inbound Telegram updates from Tower via gRPC
/// and dispatches them to ITelegramDispatcher (implemented by TelegramBotService).
///
/// Only active when MediaBoxSettings.UseTowerTelegram == true.
/// When the flag is off this service returns immediately from ExecuteAsync, doing
/// nothing — Task 11 won't even register it when the flag is off, but we guard
/// defensively here as well.
///
/// Startup sequence:
///   1. Push MediaBox's current subscribers + admin to Tower (SyncSubscribers).
///   2. Open StreamUpdates; for each update:
///        - Message  → dispatcher.HandleCommandAsync(chatId, text, ct)
///        - Callback → answer via AnswerCallback RPC first, then dispatcher.HandleCallbackAsync(...)
///   3. On stream drop / RpcException: log, wait with exponential back-off, reconnect.
///   4. Clean cancellation → exit.
/// </summary>
public class TowerUpdateConsumer(
    ITelegramDispatcher dispatcher,
    TelegramAuthStore authStore,
    IOptionsMonitor<MediaBoxSettings> settings,
    ILogger<TowerUpdateConsumer> logger) : BackgroundService
{
    // Back-off: 2s, 4s, 8s, 16s, 30s (cap).
    private static readonly TimeSpan[] BackoffDelays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(16),
        TimeSpan.FromSeconds(30)
    ];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!settings.CurrentValue.UseTowerTelegram)
        {
            logger.LogInformation("TowerUpdateConsumer: UseTowerTelegram=false — consumer disabled.");
            return;
        }

        var url = settings.CurrentValue.TowerGrpcUrl;
        logger.LogInformation("TowerUpdateConsumer: starting. Connecting to Tower at {Url}", url);

        using var channel = GrpcChannel.ForAddress(url);
        var client = new TowerTelegram.TowerTelegramClient(channel);

        // ── Step 1: push current subscribers to Tower once on start ───────────
        await SyncSubscribersAsync(client, ct);

        // ── Step 2: streaming loop ────────────────────────────────────────────
        int backoffIndex = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                logger.LogInformation("TowerUpdateConsumer: opening StreamUpdates...");
                using var call = client.StreamUpdates(new StreamRequest { ClientId = "mediabox" }, cancellationToken: ct);

                // Reset back-off on successful connect.
                backoffIndex = 0;

                await foreach (var update in call.ResponseStream.ReadAllAsync(ct))
                {
                    await DispatchUpdateAsync(client, update, ct);
                }

                // Server closed the stream cleanly — reconnect.
                logger.LogWarning("TowerUpdateConsumer: StreamUpdates ended cleanly; reconnecting...");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                logger.LogInformation("TowerUpdateConsumer: cancellation requested — stopping.");
                break;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && ct.IsCancellationRequested)
            {
                logger.LogInformation("TowerUpdateConsumer: gRPC call cancelled — stopping.");
                break;
            }
            catch (RpcException ex)
            {
                logger.LogWarning(ex,
                    "TowerUpdateConsumer: gRPC error ({Status}). Reconnecting after back-off.",
                    ex.StatusCode);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "TowerUpdateConsumer: unexpected error. Reconnecting after back-off.");
            }

            // Back-off before reconnect.
            var delay = BackoffDelays[Math.Min(backoffIndex, BackoffDelays.Length - 1)];
            backoffIndex++;
            logger.LogInformation("TowerUpdateConsumer: waiting {Delay} before reconnect...", delay);

            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("TowerUpdateConsumer: stopped.");
    }

    // ── Subscriber sync ───────────────────────────────────────────────────────

    private async Task SyncSubscribersAsync(TowerTelegram.TowerTelegramClient client, CancellationToken ct)
    {
        try
        {
            var adminChatId = authStore.GetAdminChatId();
            var allSubscribers = authStore.GetAllSubscribers();

            var list = new SubscriberList();

            // Include the admin as a subscriber with is_admin=true if present.
            if (adminChatId.HasValue)
            {
                // Find admin details from the subscriber list if they're there, else use minimal info.
                var adminSub = allSubscribers.FirstOrDefault(s => s.ChatId == adminChatId.Value);
                list.Subscribers.Add(new Subscriber
                {
                    ChatId = adminChatId.Value,
                    Name = adminSub != null
                        ? $"{adminSub.FirstName} {adminSub.LastName}".Trim()
                        : "Admin",
                    IsAdmin = true
                });
            }

            // Add all non-admin subscribers.
            foreach (var sub in allSubscribers)
            {
                if (adminChatId.HasValue && sub.ChatId == adminChatId.Value)
                    continue; // Already added as admin above.

                list.Subscribers.Add(new Subscriber
                {
                    ChatId = sub.ChatId,
                    Name = $"{sub.FirstName} {sub.LastName}".Trim(),
                    IsAdmin = false
                });
            }

            logger.LogInformation(
                "TowerUpdateConsumer: syncing {Count} subscriber(s) to Tower (admin={AdminId})...",
                list.Subscribers.Count,
                adminChatId?.ToString() ?? "none");

            var ack = await client.SyncSubscribersAsync(list, cancellationToken: ct);

            if (ack.Ok)
                logger.LogInformation("TowerUpdateConsumer: SyncSubscribers OK.");
            else
                logger.LogWarning("TowerUpdateConsumer: SyncSubscribers returned ok=false.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Propagate clean cancellation.
        }
        catch (Exception ex)
        {
            // A sync failure is not fatal — the streaming loop will still proceed.
            logger.LogWarning(ex, "TowerUpdateConsumer: SyncSubscribers failed (Tower may be offline). Continuing to streaming loop.");
        }
    }

    // ── Update dispatch ───────────────────────────────────────────────────────

    private async Task DispatchUpdateAsync(TowerTelegram.TowerTelegramClient client, Update update, CancellationToken ct)
    {
        switch (update.KindCase)
        {
            case Update.KindOneofCase.Message:
                var msg = update.Message;
                logger.LogDebug("TowerUpdateConsumer: message from chatId={ChatId}: {Text}", msg.ChatId, msg.Text);
                await dispatcher.HandleCommandAsync(msg.ChatId, msg.Text, ct);
                break;

            case Update.KindOneofCase.Callback:
                var cb = update.Callback;
                logger.LogDebug(
                    "TowerUpdateConsumer: callback from chatId={ChatId}, data={Data}",
                    cb.ChatId, cb.Data);

                // IMPORTANT: answer the callback FIRST (remove Telegram's "loading" spinner),
                // then dispatch — mirroring the order in TelegramBotService's local poll path.
                try
                {
                    await client.AnswerCallbackAsync(
                        new AnswerCallbackRequest { CallbackId = cb.CallbackId },
                        cancellationToken: ct);
                }
                catch (Exception ex)
                {
                    // A failed answer is non-fatal: the spinner will linger for the user
                    // but the underlying action should still proceed.
                    logger.LogWarning(ex, "TowerUpdateConsumer: AnswerCallback failed for {CallbackId}", cb.CallbackId);
                }

                await dispatcher.HandleCallbackAsync(cb.ChatId, cb.CallbackId, cb.Data, cb.MessageId, ct);
                break;

            default:
                logger.LogDebug("TowerUpdateConsumer: received unknown update kind {Kind}; ignoring.", update.KindCase);
                break;
        }
    }
}
