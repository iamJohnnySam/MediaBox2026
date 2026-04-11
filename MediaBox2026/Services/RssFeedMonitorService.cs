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
        logger.LogInformation("RSS feed monitor waiting for Telegram readiness...");
        await state.WaitForTelegramReadyAsync(ct);
        await Task.Delay(TimeSpan.FromSeconds(30), ct);
        logger.LogInformation("RSS feed monitor started");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CheckFeedAsync(ct);
                await CheckPendingQualityAsync(ct);
                state.LastRssCheck = DateTime.Now;
                state.NotifyChange();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "RSS feed check error");
            }

            await Task.Delay(TimeSpan.FromMinutes(settings.CurrentValue.RssFeedCheckMinutes), ct);
        }
    }

    private async Task CheckFeedAsync(CancellationToken ct)
    {
        var feedUrl = settings.CurrentValue.RssFeedUrl;
        if (string.IsNullOrWhiteSpace(feedUrl)) return;

        using var http = httpFactory.CreateClient();
        var xml = await http.GetStringAsync(feedUrl, ct);
        var doc = XDocument.Parse(xml);
        var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;
        var items = doc.Descendants(ns + "item").ToList();

        if (items.Count == 0)
            items = doc.Descendants("item").ToList();

        logger.LogInformation("RSS feed returned {Count} items", items.Count);

        foreach (var item in items)
        {
            var title = item.Element(ns + "title")?.Value ?? item.Element("title")?.Value ?? "";
            var guid = item.Element(ns + "guid")?.Value ?? item.Element("guid")?.Value ?? title;
            var link = item.Element(ns + "link")?.Value ?? item.Element("link")?.Value ?? "";

            var enclosure = item.Element(ns + "enclosure") ?? item.Element("enclosure");
            var torrentUrl = enclosure?.Attribute("url")?.Value ?? link;

            if (string.IsNullOrWhiteSpace(torrentUrl) || string.IsNullOrWhiteSpace(title))
                continue;

            if (db.ProcessedRssItems.Exists(r => r.Guid == guid))
                continue;

            var parsed = FileNameParser.Parse(title);
            if (!parsed.IsTvShow) continue;

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
                MarkProcessed(guid, title);
                continue;
            }

            var quality = FileNameParser.DetectQuality(title);
            if (FileNameParser.IsQualityAcceptable(quality))
            {
                logger.LogInformation("Downloading: {Title}", title);
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
                        CheckCount = 1,
                        Status = PendingStatus.WaitingForQuality
                    });
                    logger.LogInformation("Quality too high ({Quality}), waiting: {Title}", quality, title);
                }
                else
                {
                    existing.CheckCount++;
                    existing.TorrentUrl = torrentUrl;
                    existing.Quality = quality;
                    db.PendingDownloads.Update(existing);
                }
                MarkProcessed(guid, title);
            }
        }
    }

    private async Task CheckPendingQualityAsync(CancellationToken ct)
    {
        var waitHours = settings.CurrentValue.QualityWaitHours;
        var pending = db.PendingDownloads
            .Find(p => p.Status == PendingStatus.WaitingForQuality)
            .ToList();

        foreach (var item in pending)
        {
            var elapsed = DateTime.UtcNow - item.FirstSeen;
            if (elapsed.TotalHours < waitHours || item.AskedUser) continue;

            item.AskedUser = true;
            db.PendingDownloads.Update(item);

            var callbackId = Guid.NewGuid().ToString("N")[..8];
            var tcs = new TaskCompletionSource<string>();
            telegram.PendingCallbacks[callbackId] = tcs;

            await telegram.SendInlineKeyboardAsync(
                $"⚠️ {item.RssTitle}\nOnly {item.Quality} available after {waitHours}h. Download anyway?",
                [
                    [
                        new InlineButton { Text = "✅ Yes", CallbackData = $"{callbackId}:yes" },
                        new InlineButton { Text = "❌ No", CallbackData = $"{callbackId}:no" }
                    ]
                ], ct);

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
