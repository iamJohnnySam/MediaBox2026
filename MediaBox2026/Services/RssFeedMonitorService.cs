using System.Xml.Linq;
using MediaBox2026.Models;
using Microsoft.Extensions.Options;

namespace MediaBox2026.Services;

public class RssFeedMonitorService(
    MediaDatabase db,
    MediaCatalogService catalog,
    TransmissionClient transmission,
    ITelegramNotifier telegram,
    MediaBoxState state,
    IOptionsMonitor<MediaBoxSettings> settings,
    IHttpClientFactory httpFactory,
    ILogger<RssFeedMonitorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("📰 RSS feed monitor waiting for Telegram readiness...");

        try
        {
            await state.WaitForTelegramReadyAsync(ct);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("RSS feed monitor cancelled during Telegram wait");
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(30), ct);

        logger.LogInformation("🚀 RSS feed monitor started");
        logger.LogInformation("Quality wait hours: {Hours}h, RSS check interval: {Minutes} minutes", 
            settings.CurrentValue.QualityWaitHours, 
            settings.CurrentValue.RssFeedCheckMinutes);
        logger.LogInformation("RSS feed URL: {Url}", settings.CurrentValue.RssFeedUrl);

        int consecutiveFailures = 0;
        const int maxConsecutiveFailures = 5;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                logger.LogInformation("=== RSS Feed Check Cycle Starting ===");
                var checkStart = DateTime.UtcNow;

                await CheckFeedAsync(ct);
                await CheckPendingQualityAsync(ct);

                state.LastRssCheck = DateTime.Now;
                state.NotifyChange();

                consecutiveFailures = 0; // Reset on success
                var duration = DateTime.UtcNow - checkStart;
                logger.LogInformation("✅ RSS feed check cycle completed in {Duration:F1}s", duration.TotalSeconds);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) 
            { 
                logger.LogInformation("RSS feed monitor shutting down...");
                break; 
            }
            catch (HttpRequestException hex)
            {
                consecutiveFailures++;
                logger.LogError(hex, "❌ RSS feed HTTP error (consecutive failures: {Count}/{Max})", consecutiveFailures, maxConsecutiveFailures);

                if (consecutiveFailures >= maxConsecutiveFailures)
                {
                    logger.LogCritical("🚨 RSS feed monitor reached max consecutive failures. Increasing retry delay.");
                    await Task.Delay(TimeSpan.FromMinutes(settings.CurrentValue.RssFeedCheckMinutes * 2), ct);
                    consecutiveFailures = 0; // Reset after extended delay
                }
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                logger.LogError(ex, "❌ RSS feed check error (consecutive failures: {Count}/{Max})", consecutiveFailures, maxConsecutiveFailures);
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(settings.CurrentValue.RssFeedCheckMinutes), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                logger.LogInformation("RSS feed monitor shutting down...");
                break;
            }
        }

        logger.LogInformation("🛑 RSS feed monitor stopped");
    }

    private async Task CheckFeedAsync(CancellationToken ct)
    {
        var feedUrl = settings.CurrentValue.RssFeedUrl;
        if (string.IsNullOrWhiteSpace(feedUrl))
        {
            logger.LogError("❌ RSS feed URL is not configured. Skipping feed check.");
            return;
        }

        logger.LogInformation("📡 Fetching RSS feed from: {Url}", feedUrl);
        if (string.IsNullOrWhiteSpace(feedUrl)) return;

        using var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30); // Prevent hanging
        http.DefaultRequestHeaders.Add("User-Agent", "MediaBox2026/1.0");

        var xml = await http.GetStringAsync(feedUrl, ct);
        var doc = XDocument.Parse(xml);
        var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;
        var items = doc.Descendants(ns + "item").ToList();

        if (items.Count == 0)
            items = doc.Descendants("item").ToList();

        logger.LogInformation("RSS feed returned {Count} items", items.Count);

        int processedCount = 0, skippedAlreadyProcessed = 0, skippedNotTvShow = 0, skippedMissingData = 0;

        foreach (var item in items)
        {
            var title = item.Element(ns + "title")?.Value ?? item.Element("title")?.Value ?? "";
            var guid = item.Element(ns + "guid")?.Value ?? item.Element("guid")?.Value ?? title;
            var link = item.Element(ns + "link")?.Value ?? item.Element("link")?.Value ?? "";
            var pubDateStr = item.Element(ns + "pubDate")?.Value ?? item.Element("pubDate")?.Value;

            DateTime? pubDate = null;
            if (!string.IsNullOrWhiteSpace(pubDateStr) && DateTime.TryParse(pubDateStr, out var parsedDate))
            {
                pubDate = parsedDate.ToUniversalTime();
            }

            var enclosure = item.Element(ns + "enclosure") ?? item.Element("enclosure");
            var torrentUrl = enclosure?.Attribute("url")?.Value ?? link;

            if (string.IsNullOrWhiteSpace(torrentUrl) || string.IsNullOrWhiteSpace(title))
            {
                skippedMissingData++;
                continue;
            }

            if (db.ProcessedRssItems.Exists(r => r.Guid == guid))
            {
                skippedAlreadyProcessed++;
                continue;
            }

            var parsed = FileNameParser.Parse(title);
            if (!parsed.IsTvShow)
            {
                skippedNotTvShow++;
                logger.LogDebug("Skipping non-TV item: {Title}", title);
                continue;
            }

            logger.LogInformation("Processing TV show: {Title} (S{Season}E{Episode})", title, parsed.Season, parsed.Episode);
            processedCount++;

            var existingShow = catalog.FindTvShow(parsed.CleanName, parsed.Year);
            if (existingShow != null)
            {
                logger.LogDebug("Found existing show: {ShowName}, Latest Season: {Season}", existingShow.Name, existingShow.LatestSeason);

                if (parsed.Season!.Value < existingShow.LatestSeason)
                {
                    logger.LogInformation("Skipping old season: {Title} (S{Season} < S{LatestSeason})", title, parsed.Season.Value, existingShow.LatestSeason);
                    MarkProcessed(guid, title);
                    continue;
                }

                if (catalog.HasEpisode(parsed.CleanName, parsed.Season.Value, parsed.Episode!.Value, parsed.Year))
                {
                    logger.LogInformation("Skipping existing episode: {Title} (already have S{Season}E{Episode})", title, parsed.Season.Value, parsed.Episode.Value);
                    MarkProcessed(guid, title);
                    continue;
                }
            }
            else
            {
                logger.LogDebug("No existing show found for: {CleanName}", parsed.CleanName);
            }

            // Already dispatched to Transmission for this episode — skip duplicates from different release groups
            if (db.DispatchedEpisodes.Exists(d =>
                d.ShowName == parsed.CleanName && d.Season == parsed.Season!.Value && d.Episode == parsed.Episode!.Value))
            {
                logger.LogDebug("Skipping already dispatched: {Title}", title);
                MarkProcessed(guid, title);
                continue;
            }

            var quality = FileNameParser.DetectQuality(title);
            logger.LogInformation("Quality detected for {Title}: {Quality} (Acceptable: {Acceptable})", 
                title, quality, FileNameParser.IsQualityAcceptable(quality));
            if (FileNameParser.IsQualityAcceptable(quality))
            {
                logger.LogInformation("Quality acceptable, downloading immediately: {Title}", title);
                var added = await transmission.AddTorrentAsync(torrentUrl, ct);
                if (added)
                {
                    await telegram.SendMessageAsync($"📥 New download: {title}", ct);
                    state.AddActivity($"Started download: {title}");
                    db.DispatchedEpisodes.Insert(new DispatchedEpisode
                    {
                        ShowName = parsed.CleanName,
                        Season = parsed.Season!.Value,
                        Episode = parsed.Episode!.Value,
                        DispatchedDate = DateTime.UtcNow
                    });
                }
                MarkProcessed(guid, title);

                var pendingDupe = db.PendingDownloads.FindOne(p =>
                    p.ShowName == parsed.CleanName &&
                    p.Season == parsed.Season &&
                    p.Episode == parsed.Episode &&
                    p.Status == PendingStatus.WaitingForQuality);
                if (pendingDupe != null)
                {
                    pendingDupe.Status = PendingStatus.Downloaded;
                    db.PendingDownloads.Update(pendingDupe);
                }
            }
            else
            {
                var existing = db.PendingDownloads.FindOne(p =>
                    p.ShowName == parsed.CleanName &&
                    p.Season == parsed.Season &&
                    p.Episode == parsed.Episode &&
                    p.Status == PendingStatus.WaitingForQuality);

                if (existing == null)
                {
                    db.PendingDownloads.Insert(new PendingDownload
                    {
                        RssTitle = title,
                        TorrentUrl = torrentUrl,
                        Quality = quality,
                        ShowName = parsed.CleanName,
                        Season = parsed.Season!.Value,
                        Episode = parsed.Episode!.Value,
                        FirstSeen = DateTime.UtcNow,
                        RssPublishDate = pubDate ?? DateTime.UtcNow,
                        CheckCount = 1,
                        Status = PendingStatus.WaitingForQuality
                    });
                    logger.LogInformation("⏳ Quality too high ({Quality}), added to pending downloads: {Title}", quality, title);
                    logger.LogInformation("Will ask user about this download after {Hours}h wait period", settings.CurrentValue.QualityWaitHours);
                }
                else
                {
                    existing.CheckCount++;
                    existing.TorrentUrl = torrentUrl;
                    existing.Quality = quality;
                    db.PendingDownloads.Update(existing);
                    logger.LogDebug("Updated existing pending download (CheckCount: {Count}): {Title}", existing.CheckCount, title);
                }
                MarkProcessed(guid, title);
            }
        }

        logger.LogInformation("RSS feed processing complete: {Processed} processed, {AlreadyProcessed} already processed, {NotTv} not TV shows, {MissingData} missing data",
            processedCount, skippedAlreadyProcessed, skippedNotTvShow, skippedMissingData);
    }

    private async Task CheckPendingQualityAsync(CancellationToken ct)
    {
        var waitHours = settings.CurrentValue.QualityWaitHours;
        var pending = db.PendingDownloads
            .Find(p => p.Status == PendingStatus.WaitingForQuality)
            .ToList();

        logger.LogInformation("=== CheckPendingQuality started at {Time} UTC ===", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
        logger.LogInformation("Checking {Count} pending download(s) for quality approval (wait period: {Hours}h)", pending.Count, waitHours);

        if (pending.Count == 0)
        {
            logger.LogInformation("No pending downloads found. Skipping quality check.");
            return;
        }

        int readyToAsk = 0, stillWaiting = 0, recentlyAsked = 0, alreadyHave = 0;

        foreach (var item in pending)
        {
            var publishDate = item.RssPublishDate ?? item.FirstSeen;
            var elapsed = DateTime.UtcNow - publishDate;
            logger.LogInformation("Pending item: {Title}, Quality: {Quality}, Elapsed: {Hours:F1}h (since RSS publish), LastAsked: {LastAsked}",
                item.RssTitle, item.Quality, elapsed.TotalHours, item.LastAsked?.ToString("yyyy-MM-dd HH:mm") ?? "Never");

            // Check if episode was downloaded since this pending item was created
            if (catalog.HasEpisode(item.ShowName, item.Season, item.Episode, null))
            {
                alreadyHave++;
                logger.LogInformation("✅ Episode already in library, removing from pending: {Title}", item.RssTitle);
                item.Status = PendingStatus.Downloaded;
                db.PendingDownloads.Update(item);
                continue;
            }

            // Check if episode was dispatched (downloaded via Transmission) since this pending item was created
            if (db.DispatchedEpisodes.Exists(d =>
                d.ShowName == item.ShowName && d.Season == item.Season && d.Episode == item.Episode))
            {
                alreadyHave++;
                logger.LogInformation("✅ Episode already dispatched, removing from pending: {Title}", item.RssTitle);
                item.Status = PendingStatus.Downloaded;
                db.PendingDownloads.Update(item);
                continue;
            }

            if (elapsed.TotalHours < waitHours)
            {
                stillWaiting++;
                logger.LogInformation("⏳ Still waiting for {Title}: {Elapsed:F1}h < {Required}h (since RSS publish)", item.RssTitle, elapsed.TotalHours, waitHours);
                continue;
            }

            // Check if we already asked and message might have been deleted
            bool shouldReAsk = false;
            if (item.LastAsked.HasValue)
            {
                var hoursSinceAsked = (DateTime.UtcNow - item.LastAsked.Value).TotalHours;
                if (hoursSinceAsked < 24)
                {
                    recentlyAsked++;
                    var nextAskIn = 24 - hoursSinceAsked;
                    logger.LogInformation("🔄 Already asked recently for {Title}, will retry in {Hours:F1}h", item.RssTitle, nextAskIn);
                    continue;
                }
                else
                {
                    // More than 24 hours passed - user might have missed it or deleted the message
                    shouldReAsk = true;
                    logger.LogInformation("⚠️ Re-asking for {Title} - 24+ hours passed since last ask", item.RssTitle);
                }
            }

            readyToAsk++;
            logger.LogInformation("📱 Sending quality approval request to user for: {Title} ({Quality})", item.RssTitle, item.Quality);

            try
            {
                item.AskedUser = true;
                item.LastAsked = DateTime.UtcNow;

                var callbackId = Guid.NewGuid().ToString("N")[..8];
                var tcs = new TaskCompletionSource<string>();
                telegram.PendingCallbacks[callbackId] = tcs;

                logger.LogInformation("Created callback ID {CallbackId} for {Title}", callbackId, item.RssTitle);

                var messageId = await telegram.SendInlineKeyboardAsync(
                    $"⚠️ {item.RssTitle}\nOnly {item.Quality} available after {waitHours}h. Download anyway?",
                    [
                        [
                            new InlineButton { Text = "✅ Yes", CallbackData = $"{callbackId}:yes" },
                            new InlineButton { Text = "❌ No", CallbackData = $"{callbackId}:no" }
                        ]
                    ], ct);

                if (messageId.HasValue)
                {
                    item.TelegramMessageId = messageId.Value;
                    logger.LogInformation("✅ Quality approval notification sent successfully for: {Title} (MessageId: {MessageId})", item.RssTitle, messageId.Value);
                }
                else
                {
                    logger.LogError("❌ Failed to send Telegram notification for: {Title} - No message ID returned", item.RssTitle);
                }

                db.PendingDownloads.Update(item);
                logger.LogInformation("Updated pending download record for: {Title}", item.RssTitle);

                // Start background task to wait for user response
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromHours(24));
                        var result = await tcs.Task.WaitAsync(cts.Token);
                        telegram.PendingCallbacks.TryRemove(callbackId, out _);

                        if (result == "yes")
                        {
                            var added = await transmission.AddTorrentAsync(item.TorrentUrl, ct);
                            if (added)
                            {
                                item.Status = PendingStatus.Downloaded;
                                await telegram.SendMessageAsync($"📥 Downloading: {item.RssTitle}", ct);
                                state.AddActivity($"Quality-approved download: {item.RssTitle}");
                                db.DispatchedEpisodes.Insert(new DispatchedEpisode
                                {
                                    ShowName = item.ShowName,
                                    Season = item.Season,
                                    Episode = item.Episode,
                                    DispatchedDate = DateTime.UtcNow
                                });
                            }
                        }
                        else
                        {
                            item.Status = PendingStatus.Rejected;
                        }
                        db.PendingDownloads.Update(item);
                    }
                    catch
                    {
                        telegram.PendingCallbacks.TryRemove(callbackId, out _);
                    }
                }, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "❌ Exception while sending notification for: {Title}", item.RssTitle);
            }
        }

        logger.LogInformation("CheckPendingQuality complete: {Ready} notifications sent, {Waiting} still waiting, {Recent} recently asked, {AlreadyHave} already in library",
            readyToAsk, stillWaiting, recentlyAsked, alreadyHave);
    }

    private void MarkProcessed(string guid, string title)
    {
        if (!db.ProcessedRssItems.Exists(r => r.Guid == guid))
        {
            db.ProcessedRssItems.Insert(new ProcessedRssItem
            {
                Guid = guid,
                Title = title,
                ProcessedDate = DateTime.UtcNow
            });
        }
    }
}
