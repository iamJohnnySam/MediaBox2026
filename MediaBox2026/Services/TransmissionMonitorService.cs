using Microsoft.Extensions.Options;
using MediaBox2026.Models;

namespace MediaBox2026.Services;

public class TransmissionMonitorService(
    TransmissionClient transmission,
    MediaDatabase db,
    ITelegramNotifier telegram,
    MediaBoxState state,
    IOptionsMonitor<MediaBoxSettings> settings,
    ILogger<TransmissionMonitorService> logger) : BackgroundService
{
    private int _consecutiveFailures = 0;
    private const int MaxConsecutiveFailures = 5;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("💾 Transmission monitor waiting for Telegram readiness...");

        try
        {
            await state.WaitForTelegramReadyAsync(ct);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Transmission monitor cancelled during Telegram wait");
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(20), ct);
        logger.LogInformation("🚀 Transmission monitor started");
        logger.LogInformation("Check interval: {Minutes} minutes", settings.CurrentValue.TransmissionCheckMinutes);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                logger.LogInformation("=== Transmission Monitor Cycle Starting ===");
                if (_consecutiveFailures > 0)
                {
                    logger.LogWarning("⚠️ Consecutive failures: {Count}/{Max}", _consecutiveFailures, MaxConsecutiveFailures);
                }

                var checkStart = DateTime.UtcNow;
                await MonitorAsync(ct);
                _consecutiveFailures = 0; // Reset on success

                var duration = DateTime.UtcNow - checkStart;
                logger.LogInformation("✅ Transmission monitor cycle completed in {Duration:F1}s", duration.TotalSeconds);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                logger.LogInformation("🛑 Transmission monitor shutting down...");
                break;
            }
            catch (HttpRequestException hex)
            {
                _consecutiveFailures++;
                logger.LogError(hex, "❌ Transmission HTTP error (consecutive failures: {Count}/{Max})", _consecutiveFailures, MaxConsecutiveFailures);

                if (_consecutiveFailures >= MaxConsecutiveFailures)
                {
                    logger.LogCritical("🚨 Transmission monitor reached max consecutive failures. Increasing retry delay.");
                    await Task.Delay(TimeSpan.FromMinutes(settings.CurrentValue.TransmissionCheckMinutes * 2), ct);
                    _consecutiveFailures = 0; // Reset after extended delay
                    continue;
                }
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                logger.LogError(ex, "❌ Transmission monitor error (consecutive failures: {Count}/{Max})", _consecutiveFailures, MaxConsecutiveFailures);
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(settings.CurrentValue.TransmissionCheckMinutes), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                logger.LogInformation("🛑 Transmission monitor shutting down during delay...");
                break;
            }
        }
    }

    private async Task MonitorAsync(CancellationToken ct)
    {
        var torrents = await transmission.GetTorrentsAsync(ct);
        var activeCount = torrents.Count(t => !t.IsFinished);
        var completedCount = torrents.Count(t => t.IsFinished);

        logger.LogDebug("Retrieved {Total} torrents: {Active} active, {Completed} completed", 
            torrents.Count, activeCount, completedCount);

        state.ActiveDownloads = activeCount;
        state.NotifyChange();

        // Check for new large torrents from RSS (>1GB)
        const long oneGigabyte = 1_073_741_824; // 1GB in bytes
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var recentWindow = 300; // 5 minutes window to catch newly added torrents

        foreach (var torrent in torrents.Where(t => !t.IsFinished && t.TotalSize > oneGigabyte))
        {
            // Check if this is a recently added torrent (within last 5 minutes)
            if (torrent.DateAdded > 0 && (now - torrent.DateAdded) <= recentWindow)
            {
                // Check if we've already processed this torrent
                var existing = db.PendingLargeTorrents.FindOne(p => p.TorrentId == torrent.Id);
                if (existing == null)
                {
                    // New large torrent detected - pause it
                    var paused = await transmission.PauseTorrentAsync(torrent.Id, ct);
                    if (paused)
                    {
                        logger.LogInformation("⏸️ Paused large torrent from RSS: {Name} ({Size:N2} GB)", 
                            torrent.Name, torrent.TotalSize / 1_073_741_824.0);

                        // Track this torrent in database
                        db.PendingLargeTorrents.Insert(new PendingLargeTorrent
                        {
                            TorrentId = torrent.Id,
                            TorrentName = torrent.Name,
                            TotalSize = torrent.TotalSize,
                            AddedDate = DateTime.UtcNow,
                            AskedUser = false,
                            Status = LargeTorrentStatus.Paused
                        });

                        state.AddActivity($"Paused large torrent: {torrent.Name}");
                    }
                }
            }
        }

        // Check pending large torrents and ask for approval if not yet asked
        await CheckPendingLargeTorrentsAsync(ct);

        var completed = torrents.Where(t => t.IsFinished).ToList();
        if (completed.Count > 0)
        {
            logger.LogInformation("📦 Processing {Count} completed torrent(s)", completed.Count);
        }

        foreach (var torrent in completed)
        {
            logger.LogInformation("🗑️ Removing completed torrent: {Name}", torrent.Name);
            await transmission.RemoveTorrentAsync(torrent.Id, deleteData: false, ct);
            state.AddActivity($"Torrent completed: {torrent.Name}");

            // Clean up any pending large torrent record
            db.PendingLargeTorrents.DeleteMany(p => p.TorrentId == torrent.Id);
        }

        if (completed.Count > 0)
        {
            state.ActiveDownloads = torrents.Count - completed.Count;
            state.NotifyChange();
        }
    }

    private async Task CheckPendingLargeTorrentsAsync(CancellationToken ct)
    {
        var pending = db.PendingLargeTorrents
            .Find(p => p.Status == LargeTorrentStatus.Paused && !p.AskedUser)
            .ToList();

        if (pending.Count > 0)
        {
            logger.LogInformation("⚠️ Found {Count} pending large torrent(s) requiring approval", pending.Count);
        }

        foreach (var item in pending)
        {
            item.AskedUser = true;
            db.PendingLargeTorrents.Update(item);

            var callbackId = Guid.NewGuid().ToString("N")[..8];
            var tcs = new TaskCompletionSource<string>();
            telegram.PendingCallbacks[callbackId] = tcs;

            var sizeGB = item.TotalSize / 1_073_741_824.0;
            await telegram.SendInlineKeyboardAsync(
                $"⚠️ Large torrent detected from RSS feed\n\n" +
                $"📦 {item.TorrentName}\n" +
                $"📊 Size: {sizeGB:N2} GB\n\n" +
                $"This torrent has been paused. Resume download?",
                [
                    [
                        new InlineButton { Text = "✅ Resume", CallbackData = $"{callbackId}:resume" },
                        new InlineButton { Text = "❌ Cancel", CallbackData = $"{callbackId}:cancel" }
                    ]
                ], ct);

            _ = Task.Run(async () =>
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromHours(24));
                    var result = await tcs.Task.WaitAsync(cts.Token);
                    telegram.PendingCallbacks.TryRemove(callbackId, out _);

                    if (result == "resume")
                    {
                        var resumed = await transmission.ResumeTorrentAsync(item.TorrentId, ct);
                        if (resumed)
                        {
                            item.Status = LargeTorrentStatus.Approved;
                            await telegram.SendMessageAsync($"✅ Resumed download: {item.TorrentName}", ct);
                            state.AddActivity($"Large torrent approved: {item.TorrentName}");
                        }
                    }
                    else
                    {
                        item.Status = LargeTorrentStatus.Rejected;
                        await transmission.RemoveTorrentAsync(item.TorrentId, deleteData: true, ct);
                        await telegram.SendMessageAsync($"❌ Cancelled download: {item.TorrentName}", ct);
                        state.AddActivity($"Large torrent rejected: {item.TorrentName}");
                    }
                    db.PendingLargeTorrents.Update(item);
                }
                catch
                {
                    telegram.PendingCallbacks.TryRemove(callbackId, out _);
                }
            }, ct);
        }
    }
}
