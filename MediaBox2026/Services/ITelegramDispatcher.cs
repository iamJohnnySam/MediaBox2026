namespace MediaBox2026.Services;

/// <summary>
/// Exposes the inbound dispatch surface of TelegramBotService so it can be driven
/// by sources other than the local long-poll loop (e.g. TowerUpdateConsumer).
///
/// Important: HandleCallbackAsync does NOT answer the callback query (the "loading"
/// spinner acknowledgement). That is the CALLER's responsibility:
///   - Local poll path: TelegramBotService.HandleCallbackAsync(JsonElement) answers via
///     the Telegram HTTP API (answerCallbackQuery), then calls this method.
///   - gRPC path: TowerUpdateConsumer calls client.AnswerCallbackAsync(...) first,
///     then calls this method.
/// </summary>
public interface ITelegramDispatcher
{
    /// <summary>
    /// Dispatch a text command (or any message) from the given chat.
    /// Identical to the admin HandleCommandAsync body in TelegramBotService.
    /// </summary>
    Task HandleCommandAsync(long chatId, string text, CancellationToken ct);

    /// <summary>
    /// Dispatch a callback_query after the callback has already been acknowledged
    /// by the caller. Routes ms: prefixes to HandleMovieSessionCallbackAsync and
    /// prefix:value patterns to PendingCallbacks.
    /// </summary>
    Task HandleCallbackAsync(long chatId, string callbackId, string data, int messageId, CancellationToken ct);
}
