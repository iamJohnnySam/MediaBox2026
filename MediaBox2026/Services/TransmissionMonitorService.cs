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

    // Torrent ids already announced. Pruned to the live set each cycle, so it cannot grow.
    // ponytail: in-memory, and Transmission renumbers ids across its own restarts — worst case a
    // torrent added in the last few minutes is announced twice. Persist only if that shows up.
    private readonly HashSet<int> _announced = [];

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
                await RunOnceAsync(ct);
                _consecutiveFailures = 0; // Reset on success
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

    /// <summary>
    /// Runs one transmission monitor cycle (poll + pending large torrent approvals). This is the
    /// per-cycle work that ExecuteAsync's loop runs on a timer; it's also callable directly
    /// (e.g. via gRPC trigger).
    /// </summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        logger.LogInformation("=== Transmission Monitor Cycle Starting ===");
        if (_consecutiveFailures > 0)
        {
            logger.LogWarning("⚠️ Consecutive failures: {Count}/{Max}", _consecutiveFailures, MaxConsecutiveFailures);
        }

        var checkStart = DateTime.UtcNow;
        await MonitorAsync(ct);

        var duration = DateTime.UtcNow - checkStart;
        logger.LogInformation("✅ Transmission monitor cycle completed in {Duration:F1}s", duration.TotalSeconds);
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
                var existing = db.PendingLargeTorrents.FindOne(p => p.Hash == torrent.HashString);
                if (existing == null)
                {
                    // New large torrent detected - pause it
                    var paused = await transmission.PauseTorrentAsync(torrent.HashString, ct);
                    if (paused)
                    {
                        logger.LogInformation("⏸️ Paused large torrent from RSS: {Name} ({Size:N2} GB)", 
                            torrent.Name, torrent.TotalSize / 1_073_741_824.0);

                        // Track this torrent in database
                        db.PendingLargeTorrents.Insert(new PendingLargeTorrent
                        {
                            TorrentId = torrent.Id,
                            Hash = torrent.HashString,
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

        // Announce each newly added torrent once, whoever added it — the RSS monitor, the watchlist,
        // the episode guide, or a magnet dropped into Transmission by hand. Over-threshold ones are
        // deliberately excluded: they get their own approval prompt just below, which says more.
        _announced.IntersectWith(torrents.Select(t => t.Id));
        foreach (var torrent in torrents.Where(t =>
                     t.TotalSize <= oneGigabyte && t.DateAdded > 0 && (now - t.DateAdded) <= recentWindow))
        {
            if (!_announced.Add(torrent.Id)) continue;

            logger.LogInformation("📥 Announcing new torrent: {Name} ({Size} bytes)", torrent.Name, torrent.TotalSize);
            await telegram.SendMessageAsync(
                $"📥 Download started\n\n📦 {torrent.Name}\n📊 Size: {FormatSize(torrent.TotalSize)}", ct);
            state.AddActivity($"Download started: {torrent.Name}");
        }

        // Check pending large torrents and ask for approval if not yet asked
        await CheckPendingLargeTorrentsAsync(torrents, ct);

        var completed = torrents.Where(t => t.IsFinished).ToList();
        if (completed.Count > 0)
        {
            logger.LogInformation("📦 Processing {Count} completed torrent(s)", completed.Count);
        }

        foreach (var torrent in completed)
        {
            logger.LogInformation("🗑️ Removing completed torrent: {Name}", torrent.Name);
            await transmission.RemoveTorrentAsync(torrent.HashString, deleteData: false, ct);
            state.AddActivity($"Torrent completed: {torrent.Name}");

            // Clean up any pending large torrent record
            db.PendingLargeTorrents.DeleteMany(p => p.Hash == torrent.HashString);
        }

        if (completed.Count > 0)
        {
            state.ActiveDownloads = torrents.Count - completed.Count;
            state.NotifyChange();
        }
    }

    public static string FormatSize(long bytes) => bytes >= 1_073_741_824
        ? $"{bytes / 1_073_741_824.0:N2} GB"
        : $"{bytes / 1_048_576.0:N0} MB";

    // Re-ask an unanswered prompt after this long. Cycle-based (not an in-memory timer) so it
    // survives service restarts — otherwise AskedUser records orphan forever when the process dies.
    private const int ReAskAfterHours = 24;

    private async Task CheckPendingLargeTorrentsAsync(List<TorrentInfo> torrents, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        // Rows written before the hash was stored carry only a session id, which Transmission has
        // since renumbered. Match those back up by name so an old row still acts on its real torrent.
        foreach (var legacy in db.PendingLargeTorrents.Find(p => p.Hash == "").ToList())
        {
            var match = torrents.FirstOrDefault(t => t.Name == legacy.TorrentName);
            if (match == null) continue;
            legacy.Hash = match.HashString;
            db.PendingLargeTorrents.Update(legacy);
            logger.LogInformation("Backfilled hash for pending large torrent: {Name}", legacy.TorrentName);
        }

        // A row whose torrent is no longer in Transmission has nothing left to approve; without this
        // it would re-prompt every ReAskAfterHours forever. Only rows that already carry a hash are
        // judged — a legacy row that just failed to match above may simply be paused and unlisted.
        var live = torrents.Select(t => t.HashString).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in db.PendingLargeTorrents
                     .Find(p => p.Status == LargeTorrentStatus.Paused && p.Hash != "" && !live.Contains(p.Hash))
                     .ToList())
        {
            logger.LogInformation("Dropping pending large torrent no longer in Transmission: {Name}", gone.TorrentName);
            db.PendingLargeTorrents.DeleteMany(p => p.Id == gone.Id);
        }
        var pending = db.PendingLargeTorrents
            .Find(p => p.Status == LargeTorrentStatus.Paused &&
                       (!p.AskedUser ||
                        (p.LastAsked.HasValue && (now - p.LastAsked.Value).TotalHours >= ReAskAfterHours)))
            .ToList();

        if (pending.Count > 0)
        {
            logger.LogInformation("⚠️ Found {Count} pending large torrent(s) requiring approval", pending.Count);
        }

        foreach (var item in pending)
        {
            // Re-ask: strip the previous prompt's keyboard so only the newest one is live.
            if (item.AskedUser && item.TelegramMessageId.HasValue)
            {
                await telegram.EditMessageAsync(
                    item.TelegramMessageId.Value,
                    $"⏱️ No response in {ReAskAfterHours}h\n\n📦 {item.TorrentName}\n\nRe-sent a fresh prompt below — this one is no longer active.",
                    ct);
            }

            var callbackId = Guid.NewGuid().ToString("N")[..8];
            var tcs = new TaskCompletionSource<string>();
            telegram.PendingCallbacks[callbackId] = tcs;

            var sizeGB = item.TotalSize / 1_073_741_824.0;
            var messageId = await telegram.SendInlineKeyboardAsync(
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

            item.AskedUser = true;
            item.LastAsked = now;
            item.TelegramMessageId = messageId;
            db.PendingLargeTorrents.Update(item);

            _ = Task.Run(async () =>
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromHours(ReAskAfterHours));
                    var result = await tcs.Task.WaitAsync(cts.Token);
                    telegram.PendingCallbacks.TryRemove(callbackId, out _);

                    if (result == "resume")
                    {
                        var resumed = await transmission.ResumeTorrentAsync(item.Hash, ct);
                        if (resumed)
                        {
                            item.Status = LargeTorrentStatus.Approved;
                            if (messageId.HasValue)
                                await telegram.EditMessageAsync(messageId.Value, $"✅ Resumed\n\n📦 {item.TorrentName}\n\nDownload started.", ct);
                            else
                                await telegram.SendMessageAsync($"✅ Resumed download: {item.TorrentName}", ct);
                            state.AddActivity($"Large torrent approved: {item.TorrentName}");
                        }
                    }
                    else
                    {
                        item.Status = LargeTorrentStatus.Rejected;
                        await transmission.RemoveTorrentAsync(item.Hash, deleteData: true, ct);
                        if (messageId.HasValue)
                            await telegram.EditMessageAsync(messageId.Value, $"❌ Cancelled\n\n📦 {item.TorrentName}\n\nTorrent removed.", ct);
                        else
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
