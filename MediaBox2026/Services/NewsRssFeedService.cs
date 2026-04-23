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
    private int _consecutiveFailures = 0;
    private const int MaxConsecutiveFailures = 5;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("📰 News RSS feed monitor waiting for Telegram readiness...");

        try
        {
            await state.WaitForTelegramReadyAsync(ct);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("News RSS feed monitor cancelled during Telegram wait");
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(30), ct);
        logger.LogInformation("🚀 News RSS feed monitor started");
        logger.LogInformation("Check interval: {Minutes} minutes", settings.CurrentValue.RssFeedCheckMinutes);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                logger.LogInformation("=== News RSS Feed Check Cycle Starting ===");
                if (_consecutiveFailures > 0)
                {
                    logger.LogWarning("⚠️ Consecutive failures: {Count}/{Max}", _consecutiveFailures, MaxConsecutiveFailures);
                }

                var checkStart = DateTime.UtcNow;
                await CheckAllFeedsAsync(ct);
                _consecutiveFailures = 0; // Reset on success

                var duration = DateTime.UtcNow - checkStart;
                logger.LogInformation("✅ News RSS feed check cycle completed in {Duration:F1}s", duration.TotalSeconds);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) 
            { 
                logger.LogInformation("🛑 News RSS feed monitor shutting down...");
                break; 
            }
            catch (HttpRequestException hex)
            {
                _consecutiveFailures++;
                logger.LogError(hex, "❌ News RSS feed HTTP error (consecutive failures: {Count}/{Max})", _consecutiveFailures, MaxConsecutiveFailures);

                if (_consecutiveFailures >= MaxConsecutiveFailures)
                {
                    logger.LogCritical("🚨 News RSS feed monitor reached max consecutive failures. Increasing retry delay.");
                    await Task.Delay(TimeSpan.FromMinutes(settings.CurrentValue.RssFeedCheckMinutes * 2), ct);
                    _consecutiveFailures = 0; // Reset after extended delay
                    continue;
                }
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                logger.LogError(ex, "❌ News RSS feed check error (consecutive failures: {Count}/{Max})", _consecutiveFailures, MaxConsecutiveFailures);
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(settings.CurrentValue.RssFeedCheckMinutes), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                logger.LogInformation("🛑 News RSS feed monitor shutting down during delay...");
                break;
            }
        }
    }

    private async Task CheckAllFeedsAsync(CancellationToken ct)
    {
        var subscriptions = db.RssFeedSubscriptions.Find(s => s.IsActive).ToList();

        if (subscriptions.Count == 0)
        {
            logger.LogDebug("No active RSS feed subscriptions found");
            return;
        }

        logger.LogInformation("📡 Checking {Count} active feed subscription(s)", subscriptions.Count);

        foreach (var subscription in subscriptions)
        {
            try
            {
                await CheckSingleFeedAsync(subscription, ct);
                subscription.LastChecked = DateTime.UtcNow;
                db.RssFeedSubscriptions.Update(subscription);
            }
            catch (HttpRequestException hex)
            {
                logger.LogError(hex, "❌ HTTP error checking feed '{FeedName}': {FeedUrl}", subscription.FeedName, subscription.FeedUrl);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "❌ Error checking feed '{FeedName}': {FeedUrl}", subscription.FeedName, subscription.FeedUrl);
            }
        }
    }

    private async Task CheckSingleFeedAsync(RssFeedSubscription subscription, CancellationToken ct)
    {
        logger.LogDebug("Checking feed: {FeedName} ({FeedUrl})", subscription.FeedName, subscription.FeedUrl);

        using var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.Add("User-Agent", "MediaBox2026/1.0 (News Feed Reader)");

        string xml;
        try
        {
            xml = await http.GetStringAsync(subscription.FeedUrl, ct);
        }
        catch (HttpRequestException hex)
        {
            logger.LogError(hex, "Failed to fetch feed '{FeedName}' from {FeedUrl}", subscription.FeedName, subscription.FeedUrl);
            throw;
        }

        if (string.IsNullOrWhiteSpace(xml))
        {
            logger.LogWarning("⚠️ Feed '{FeedName}' returned empty content", subscription.FeedName);
            return;
        }

        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "❌ Failed to parse XML from feed '{FeedName}'", subscription.FeedName);
            throw;
        }
        var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;
        var items = doc.Descendants(ns + "item").ToList();

        // Try without namespace if no items found (for RSS)
        if (items.Count == 0)
            items = doc.Descendants("item").ToList();

        // Try Atom format if still no items
        if (items.Count == 0)
        {
            var atomNs = XNamespace.Get("http://www.w3.org/2005/Atom");
            items = doc.Descendants(atomNs + "entry").ToList();
            if (items.Count == 0)
                items = doc.Descendants("entry").ToList();
        }

        logger.LogDebug("Feed '{FeedName}' returned {Count} items", subscription.FeedName, items.Count);

        if (items.Count == 0)
        {
            logger.LogWarning("⚠️ No items found in feed '{FeedName}'", subscription.FeedName);
            return;
        }

        var newItemsFound = 0;
        var processedInThisCycle = new HashSet<string>();

        foreach (var item in items)
        {
            // Support both RSS and Atom formats
            var title = item.Element(ns + "title")?.Value ?? item.Element("title")?.Value ?? "";
            var guid = item.Element(ns + "guid")?.Value ?? item.Element("guid")?.Value 
                ?? item.Element(ns + "id")?.Value ?? item.Element("id")?.Value ?? title;
            var link = item.Element(ns + "link")?.Value ?? item.Element("link")?.Value
                ?? item.Element(ns + "link")?.Attribute("href")?.Value ?? "";
            var description = item.Element(ns + "description")?.Value ?? item.Element("description")?.Value
                ?? item.Element(ns + "summary")?.Value ?? item.Element("summary")?.Value
                ?? item.Element(ns + "content")?.Value ?? item.Element("content")?.Value ?? "";
            var pubDate = item.Element(ns + "pubDate")?.Value ?? item.Element("pubDate")?.Value
                ?? item.Element(ns + "published")?.Value ?? item.Element("published")?.Value
                ?? item.Element(ns + "updated")?.Value ?? item.Element("updated")?.Value ?? "";

            if (string.IsNullOrWhiteSpace(guid) || string.IsNullOrWhiteSpace(title))
            {
                logger.LogDebug("Skipping item with missing guid or title in feed '{FeedName}'", subscription.FeedName);
                continue;
            }

            // Check if already processed in database
            if (db.ProcessedFeedItems.Exists(r => r.SubscriptionId == subscription.Id && r.ItemGuid == guid))
                continue;

            // Check if already processed in this cycle (duplicate prevention)
            if (!processedInThisCycle.Add(guid))
            {
                logger.LogDebug("Skipping duplicate item in same cycle: {Guid}", guid);
                continue;
            }

            newItemsFound++;

            var message = $"📰 *{EscapeMarkdown(subscription.FeedName)}*\n\n*{EscapeMarkdown(title)}*";

            if (!string.IsNullOrWhiteSpace(description))
            {
                var cleanDesc = StripHtml(description);
                if (cleanDesc.Length > 300)
                    cleanDesc = cleanDesc[..300] + "...";
                message += $"\n\n{EscapeMarkdown(cleanDesc)}";
            }

            if (!string.IsNullOrWhiteSpace(link) && Uri.TryCreate(link, UriKind.Absolute, out _))
                message += $"\n\n🔗 [Read more]({link})";

            if (!string.IsNullOrWhiteSpace(pubDate))
            {
                var formattedDate = TryFormatPubDate(pubDate);
                message += $"\n\n🕒 {formattedDate}";
            }

            try
            {
                await telegram.SendMessageAsync(message, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "❌ Failed to send Telegram notification for item: {Title}", title);
                // Continue processing other items even if one fails
            }
            state.AddActivity($"News: {subscription.FeedName} - {title}");

            try
            {
                db.ProcessedFeedItems.Insert(new ProcessedFeedItem
                {
                    SubscriptionId = subscription.Id,
                    ItemGuid = guid,
                    Title = title,
                    ProcessedDate = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "❌ Failed to save processed item to database: {Title}", title);
            }
        }

        if (newItemsFound > 0)
        {
            logger.LogInformation("✅ Feed '{FeedName}': {Count} new item(s) processed", subscription.FeedName, newItemsFound);
        }
        else
        {
            logger.LogDebug("No new items found in feed '{FeedName}'", subscription.FeedName);
        }
    }

    private static string StripHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        try
        {
            // Remove HTML tags
            var text = System.Text.RegularExpressions.Regex.Replace(html, "<.*?>", string.Empty);

            // Decode HTML entities
            text = System.Net.WebUtility.HtmlDecode(text);

            // Remove extra whitespace
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");

            return text.Trim();
        }
        catch (Exception)
        {
            return html; // Return original if stripping fails
        }
    }

    private static string EscapeMarkdown(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        // Escape Telegram markdown special characters
        return text.Replace("*", "\\*")
                   .Replace("_", "\\_")
                   .Replace("[", "\\[")
                   .Replace("`", "\\`");
    }

    private static string TryFormatPubDate(string pubDate)
    {
        if (string.IsNullOrWhiteSpace(pubDate))
            return string.Empty;

        try
        {
            if (DateTime.TryParse(pubDate, out var date))
            {
                // Convert to local time and format nicely
                return date.ToLocalTime().ToString("MMM dd, yyyy HH:mm");
            }
        }
        catch
        {
            // If parsing fails, return original
        }

        return pubDate;
    }
}
