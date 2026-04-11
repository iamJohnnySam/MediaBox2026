using System.Xml.Linq;
using MediaBox2026.Models;
using Microsoft.Extensions.Options;

namespace MediaBox2026.Services;

public class NewsRssFeedService(
    MediaDatabase db,
    ITelegramNotifier telegram,
    MediaBoxState state,
    IOptionsMonitor<MediaBoxSettings> settings,
    IHttpClientFactory httpFactory,
    ILogger<NewsRssFeedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("News RSS feed monitor waiting for Telegram readiness...");
        await state.WaitForTelegramReadyAsync(ct);
        await Task.Delay(TimeSpan.FromSeconds(30), ct);
        logger.LogInformation("News RSS feed monitor started");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CheckAllFeedsAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "News RSS feed check error");
            }

            await Task.Delay(TimeSpan.FromMinutes(settings.CurrentValue.RssFeedCheckMinutes), ct);
        }
    }

    private async Task CheckAllFeedsAsync(CancellationToken ct)
    {
        var subscriptions = db.RssFeedSubscriptions.Find(s => s.IsActive).ToList();
        
        foreach (var subscription in subscriptions)
        {
            try
            {
                await CheckSingleFeedAsync(subscription, ct);
                subscription.LastChecked = DateTime.UtcNow;
                db.RssFeedSubscriptions.Update(subscription);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error checking feed: {FeedUrl}", subscription.FeedUrl);
            }
        }
    }

    private async Task CheckSingleFeedAsync(RssFeedSubscription subscription, CancellationToken ct)
    {
        using var http = httpFactory.CreateClient();
        var xml = await http.GetStringAsync(subscription.FeedUrl, ct);
        var doc = XDocument.Parse(xml);
        var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;
        var items = doc.Descendants(ns + "item").ToList();

        if (items.Count == 0)
            items = doc.Descendants("item").ToList();

        logger.LogDebug("Feed {FeedName} returned {Count} items", subscription.FeedName, items.Count);

        var newItemsFound = 0;

        foreach (var item in items)
        {
            var title = item.Element(ns + "title")?.Value ?? item.Element("title")?.Value ?? "";
            var guid = item.Element(ns + "guid")?.Value ?? item.Element("guid")?.Value ?? title;
            var link = item.Element(ns + "link")?.Value ?? item.Element("link")?.Value ?? "";
            var description = item.Element(ns + "description")?.Value ?? item.Element("description")?.Value ?? "";
            var pubDate = item.Element(ns + "pubDate")?.Value ?? item.Element("pubDate")?.Value ?? "";

            if (string.IsNullOrWhiteSpace(guid) || string.IsNullOrWhiteSpace(title))
                continue;

            if (db.ProcessedFeedItems.Exists(r => r.SubscriptionId == subscription.Id && r.ItemGuid == guid))
                continue;

            newItemsFound++;
            
            var message = $"📰 *{subscription.FeedName}*\n\n*{title}*";
            
            if (!string.IsNullOrWhiteSpace(description))
            {
                var cleanDesc = StripHtml(description);
                if (cleanDesc.Length > 200)
                    cleanDesc = cleanDesc[..200] + "...";
                message += $"\n\n{cleanDesc}";
            }
            
            if (!string.IsNullOrWhiteSpace(link))
                message += $"\n\n🔗 [Read more]({link})";
            
            if (!string.IsNullOrWhiteSpace(pubDate))
                message += $"\n\n🕒 {pubDate}";

            await telegram.SendMessageAsync(message, ct);
            state.AddActivity($"News: {subscription.FeedName} - {title}");

            db.ProcessedFeedItems.Insert(new ProcessedFeedItem
            {
                SubscriptionId = subscription.Id,
                ItemGuid = guid,
                Title = title,
                ProcessedDate = DateTime.UtcNow
            });
        }

        if (newItemsFound > 0)
        {
            logger.LogInformation("Feed {FeedName}: {Count} new items", subscription.FeedName, newItemsFound);
        }
    }

    private static string StripHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        var text = System.Text.RegularExpressions.Regex.Replace(html, "<.*?>", string.Empty);
        text = System.Net.WebUtility.HtmlDecode(text);
        return text.Trim();
    }
}
