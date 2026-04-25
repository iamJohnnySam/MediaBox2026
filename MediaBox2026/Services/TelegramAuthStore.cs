using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using MediaBox2026.Models;
using Microsoft.Extensions.Options;

namespace MediaBox2026.Services;

public class TelegramAuthStore
{
    private readonly MediaDatabase _db;
    private readonly ILogger<TelegramAuthStore> _logger;
    private readonly IOptionsMonitor<MediaBoxSettings> _settings;
    private readonly object _lock = new();

    public TelegramAuthStore(
        MediaDatabase db,
        ILogger<TelegramAuthStore> logger, 
        IOptionsMonitor<MediaBoxSettings> settings)
    {
        _db = db;
        _logger = logger;
        _settings = settings;
    }

    public long? GetAdminChatId()
    {
        // First check if admin chat ID is configured in settings
        var configuredChatId = _settings.CurrentValue.TelegramChatId;
        if (configuredChatId.HasValue)
        {
            _logger.LogInformation("Using configured Telegram admin chat ID from settings: {ChatId}", configuredChatId.Value);
            return configuredChatId.Value;
        }

        // Fall back to database
        var admin = _db.TelegramAdmins.FindOne(a => true);
        return admin?.ChatId;
    }

    public bool IsAdmin(long chatId)
    {
        var configuredChatId = _settings.CurrentValue.TelegramChatId;
        if (configuredChatId.HasValue && configuredChatId.Value == chatId)
            return true;

        return _db.TelegramAdmins.Exists(a => a.ChatId == chatId);
    }

    public bool HasAdmin()
    {
        if (_settings.CurrentValue.TelegramChatId.HasValue)
            return true;

        return _db.TelegramAdmins.Count() > 0;
    }

    public void SetAdmin(long chatId, string? username, string? firstName, string? lastName)
    {
        lock (_lock)
        {
            var existing = _db.TelegramAdmins.FindOne(a => a.ChatId == chatId);
            if (existing != null)
            {
                _logger.LogInformation("Admin already exists for chat {ChatId}", chatId);
                return;
            }

            _db.TelegramAdmins.Insert(new TelegramAdmin
            {
                ChatId = chatId,
                Username = username,
                FirstName = firstName,
                LastName = lastName,
                AuthenticatedDate = DateTime.UtcNow
            });

            _logger.LogInformation("Admin authenticated: {ChatId} (@{Username})", chatId, username ?? "unknown");
        }
    }

    public List<TelegramSubscriber> GetActiveSubscribers()
    {
        return _db.TelegramSubscribers
            .Find(s => s.IsActive && !s.IsBlocked)
            .ToList();
    }

    public List<TelegramSubscriber> GetAllSubscribers()
    {
        return _db.TelegramSubscribers.FindAll().ToList();
    }

    public bool IsSubscriber(long chatId)
    {
        var sub = _db.TelegramSubscribers.FindOne(s => s.ChatId == chatId);
        return sub != null && sub.IsActive && !sub.IsBlocked;
    }

    public bool IsBlocked(long chatId)
    {
        var sub = _db.TelegramSubscribers.FindOne(s => s.ChatId == chatId);
        return sub?.IsBlocked ?? false;
    }

    public void AddSubscriber(long chatId, string? username, string? firstName, string? lastName)
    {
        lock (_lock)
        {
            var existing = _db.TelegramSubscribers.FindOne(s => s.ChatId == chatId);
            if (existing != null)
            {
                if (existing.IsBlocked)
                {
                    _logger.LogWarning("Blocked user attempted to subscribe: {ChatId}", chatId);
                    return;
                }

                if (!existing.IsActive)
                {
                    existing.IsActive = true;
                    _db.TelegramSubscribers.Update(existing);
                    _logger.LogInformation("Subscriber reactivated: {ChatId}", chatId);
                }
                return;
            }

            _db.TelegramSubscribers.Insert(new TelegramSubscriber
            {
                ChatId = chatId,
                Username = username,
                FirstName = firstName,
                LastName = lastName,
                SubscribedDate = DateTime.UtcNow,
                IsActive = true,
                IsBlocked = false
            });

            _logger.LogInformation("New subscriber added: {ChatId} (@{Username})", chatId, username ?? "unknown");
        }
    }

    public void RemoveSubscriber(long chatId)
    {
        lock (_lock)
        {
            var subscriber = _db.TelegramSubscribers.FindOne(s => s.ChatId == chatId);
            if (subscriber != null)
            {
                subscriber.IsActive = false;
                _db.TelegramSubscribers.Update(subscriber);
                _logger.LogInformation("Subscriber deactivated: {ChatId}", chatId);
            }
        }
    }

    public void BlockSubscriber(long chatId)
    {
        lock (_lock)
        {
            var subscriber = _db.TelegramSubscribers.FindOne(s => s.ChatId == chatId);
            if (subscriber != null)
            {
                subscriber.IsBlocked = true;
                subscriber.IsActive = false;
                _db.TelegramSubscribers.Update(subscriber);
                _logger.LogInformation("Subscriber blocked: {ChatId}", chatId);
            }
        }
    }

    // Legacy method for backward compatibility
    public long? GetChatId() => GetAdminChatId();

    // Legacy method for backward compatibility - sets as admin
    public void SetChatId(long chatId) => SetAdmin(chatId, null, null, null);
}
