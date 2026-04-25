using System.Xml.Linq;
using MediaBox2026.Models;
using MediaBox2026.Services;

namespace MediaBox2026;

/// <summary>
/// Diagnostic tool to test RSS news feed functionality
/// </summary>
public static class DiagnosticNewsFeed
{
    public static async Task RunDiagnostics(IServiceProvider services)
    {
        Console.WriteLine("=== RSS News Feed Diagnostics ===\n");

        var db = services.GetRequiredService<MediaDatabase>();
        var httpFactory = services.GetRequiredService<IHttpClientFactory>();

        // Check subscriptions
        var subscriptions = db.RssFeedSubscriptions.FindAll().ToList();
        Console.WriteLine($"Total subscriptions: {subscriptions.Count}");
        Console.WriteLine($"Active subscriptions: {subscriptions.Count(s => s.IsActive)}");

        if (subscriptions.Count == 0)
        {
            Console.WriteLine("\n❌ No subscriptions found in database!");
            Console.WriteLine("   Make sure you subscribed using: /subscribe <url> <name>");
            return;
        }

        foreach (var sub in subscriptions)
        {
            Console.WriteLine($"\n📰 {sub.FeedName}");
            Console.WriteLine($"   URL: {sub.FeedUrl}");
            Console.WriteLine($"   Active: {sub.IsActive}");
            Console.WriteLine($"   Subscribed: {sub.SubscribedDate}");
            Console.WriteLine($"   Last Checked: {sub.LastChecked?.ToString() ?? "Never"}");

            var processedCount = db.ProcessedFeedItems.Count(p => p.SubscriptionId == sub.Id);
            Console.WriteLine($"   Processed Items: {processedCount}");

            // Test fetch
            if (sub.IsActive)
            {
                try
                {
                    Console.WriteLine($"   Testing fetch...");
                    using var http = httpFactory.CreateClient();
                    http.Timeout = TimeSpan.FromSeconds(30);
                    http.DefaultRequestHeaders.Add("User-Agent", "MediaBox2026/1.0 (Diagnostic)");

                    var xml = await http.GetStringAsync(sub.FeedUrl);
                    var doc = XDocument.Parse(xml);
                    var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;
                    var items = doc.Descendants(ns + "item").ToList();

                    if (items.Count == 0)
                        items = doc.Descendants("item").ToList();

                    Console.WriteLine($"   ✅ Fetch successful: {items.Count} items found");

                    if (items.Count > 0)
                    {
                        var firstItem = items.First();
                        var title = firstItem.Element(ns + "title")?.Value ?? firstItem.Element("title")?.Value ?? "";
                        var guid = firstItem.Element(ns + "guid")?.Value ?? firstItem.Element("guid")?.Value ?? title;

                        Console.WriteLine($"   First item: {title}");

                        var alreadyProcessed = db.ProcessedFeedItems.Exists(p => 
                            p.SubscriptionId == sub.Id && p.ItemGuid == guid);
                        Console.WriteLine($"   Already processed: {alreadyProcessed}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"   ❌ Fetch failed: {ex.Message}");
                }
            }
        }

        Console.WriteLine("\n=== End Diagnostics ===");
    }
}
