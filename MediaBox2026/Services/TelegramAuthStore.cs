using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace MediaBox2026.Services;

public class TelegramAuthStore
{
    private readonly string _filePath;
    private readonly ILogger<TelegramAuthStore> _logger;
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public TelegramAuthStore(ILogger<TelegramAuthStore> logger)
    {
        _logger = logger;
        _filePath = Path.Combine(AppContext.BaseDirectory, "telegram-auth.json");
    }

    public long? GetChatId()
    {
        lock (_lock)
        {
            if (!File.Exists(_filePath)) return null;
            try
            {
                var json = File.ReadAllText(_filePath);
                var data = JsonSerializer.Deserialize<AuthData>(json, JsonOpts);
                return data?.ChatId;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read Telegram auth file");
                return null;
            }
        }
    }

    public void SetChatId(long chatId)
    {
        lock (_lock)
        {
            try
            {
                var data = new AuthData { ChatId = chatId, AuthenticatedDate = DateTime.UtcNow };
                var json = JsonSerializer.Serialize(data, JsonOpts);
                File.WriteAllText(_filePath, json);
                _logger.LogInformation("Telegram chat ID saved to {Path}", _filePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write Telegram auth file");
            }
        }
    }

    private sealed class AuthData
    {
        public long ChatId { get; set; }
        public DateTime AuthenticatedDate { get; set; }
    }
}
