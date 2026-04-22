using MediaBox2026.Models;

namespace MediaBox2026.Services;

/// <summary>
/// Extension methods for safer Telegram operations
/// </summary>
public static class TelegramExtensions
{
    /// <summary>
    /// Safely sends a Telegram message with automatic error handling and logging
    /// </summary>
    public static async Task<bool> TrySendMessageAsync(
        this ITelegramNotifier telegram,
        string message,
        ILogger logger,
        CancellationToken ct = default)
    {
        try
        {
            await telegram.SendMessageAsync(message, ct);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send Telegram message: {Message}", message);
            return false;
        }
    }

    /// <summary>
    /// Safely sends a Telegram notification with inline keyboard and automatic error handling
    /// </summary>
    public static async Task<int?> TrySendInlineKeyboardAsync(
        this ITelegramNotifier telegram,
        string text,
        List<List<InlineButton>> buttons,
        ILogger logger,
        CancellationToken ct = default)
    {
        try
        {
            return await telegram.SendInlineKeyboardAsync(text, buttons, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send Telegram inline keyboard: {Text}", text);
            return null;
        }
    }
}
