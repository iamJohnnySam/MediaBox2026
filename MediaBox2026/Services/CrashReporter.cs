using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using MediaBox2026.Models;
using Microsoft.Extensions.Options;

namespace MediaBox2026.Services;

public class CrashReporter : IDisposable
{
    private readonly InMemoryLogSink _logSink;
    private readonly ITelegramNotifier _telegram;
    private readonly IOptionsMonitor<MediaBoxSettings> _settings;
    private volatile bool _isReporting;

    public CrashReporter(
        InMemoryLogSink logSink,
        ITelegramNotifier telegram,
        IOptionsMonitor<MediaBoxSettings> settings)
    {
        _logSink = logSink;
        _telegram = telegram;
        _settings = settings;
        _logSink.OnErrorLog += HandleErrorLog;
    }

    private void HandleErrorLog(LogEntry entry)
    {
        if (_isReporting) return;
        _isReporting = true;

        _ = Task.Run(async () =>
        {
            try
            {
                SaveCrashData(entry);
                var msg = $"🚨 {entry.Level}: [{ShortenCategory(entry.Category)}] {entry.Message}";
                if (msg.Length > 400)
                    msg = msg[..397] + "...";
                await _telegram.SendMessageAsync(msg);
            }
            catch { /* best-effort */ }
            finally { _isReporting = false; }
        });
    }

    public void SaveCrashData(LogEntry trigger)
    {
        try
        {
            var path = _settings.CurrentValue.CrashDataPath;
            Directory.CreateDirectory(path);

            var crashFile = Path.Combine(path, $"crash_{DateTime.Now:yyyyMMdd_HHmmss}.json");
            var recentLogs = _logSink.GetEntries()
                .TakeLast(100)
                .Select(e => new
                {
                    e.Timestamp,
                    Level = e.Level.ToString(),
                    e.Category,
                    e.Message,
                    e.Exception
                });

            var crashData = new
            {
                Trigger = new
                {
                    trigger.Timestamp,
                    Level = trigger.Level.ToString(),
                    trigger.Category,
                    trigger.Message,
                    trigger.Exception
                },
                RecentLogs = recentLogs,
                Environment = new
                {
                    MachineName = System.Environment.MachineName,
                    OS = System.Environment.OSVersion.ToString(),
                    Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                    WorkingSet = System.Environment.WorkingSet
                }
            };

            var json = JsonSerializer.Serialize(crashData, new JsonSerializerOptions { WriteIndented = true, TypeInfoResolver = new DefaultJsonTypeInfoResolver() });
            File.WriteAllText(crashFile, json);
        }
        catch { /* must not throw */ }
    }

    public void SaveUnhandledException(Exception ex, string source)
    {
        var entry = new LogEntry
        {
            Timestamp = DateTime.Now,
            Level = Microsoft.Extensions.Logging.LogLevel.Critical,
            Category = source,
            Message = $"Unhandled exception: {ex.Message}",
            Exception = ex.ToString()
        };
        SaveCrashData(entry);

        try
        {
            var msg = $"💀 CRITICAL [{source}]: {ex.Message}";
            if (msg.Length > 400) msg = msg[..397] + "...";
            _telegram.SendMessageAsync(msg).GetAwaiter().GetResult();
        }
        catch { /* best-effort */ }
    }

    private static string ShortenCategory(string category)
    {
        var lastDot = category.LastIndexOf('.');
        return lastDot >= 0 ? category[(lastDot + 1)..] : category;
    }

    public void Dispose() => _logSink.OnErrorLog -= HandleErrorLog;
}
