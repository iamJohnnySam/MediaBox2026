# RSS News Feeds Feature

MediaBox2026 now supports subscribing to general news RSS feeds and receiving Telegram notifications when new articles are published.

## Overview

The RSS News Feed feature allows you to:
- Subscribe to any RSS/Atom feed via Telegram commands
- Receive real-time notifications when new articles are published
- Manage your subscriptions (subscribe/unsubscribe)
- View all your active subscriptions

## How It Works

1. **NewsRssFeedService** - A background service that periodically checks all subscribed RSS feeds
2. **Database Storage** - Subscriptions and processed feed items are stored in SQLite
3. **Telegram Integration** - New articles trigger Telegram messages with title, description, and link

## Telegram Commands

### Subscribe to a Feed

```
/subscribe <url> <name>
```

**Example:**
```
/subscribe https://www.adaderana.lk/rss.php Ada Derana News
/subscribe https://semiwiki.com/feed/ SemiWiki Tech News
```

### View All Subscriptions

```
/feeds
```

Shows all your active RSS feed subscriptions with their names, URLs, and last check time.

### Unsubscribe from a Feed

```
/unsubscribe <name>
```

**Example:**
```
/unsubscribe Ada Derana News
```

The name matching is case-insensitive and partial matches work (e.g., `/unsubscribe ada` will match "Ada Derana News").

### Check Feed Status (Diagnostic)

```
/checkfeeds
```

This command tests all your subscribed feeds and shows:
- Total subscriptions (active/inactive)
- Last check time for each feed
- Number of processed items
- Current feed status (tests if the feed URL is reachable)
- Whether the latest item has been sent or is new

**Use this command to troubleshoot if you're not receiving notifications!**

### Manual News Check

```
/checknews
```

Shows the current check interval and reminds you to use `/checkfeeds` for diagnostics.

### Help Command

```
/help
```

Shows all available commands including the new RSS feed commands.

## Configuration

The feed checking interval is controlled by the existing `RssFeedCheckMinutes` setting in `appsettings.json`:

```json
{
  "MediaBox": {
    "RssFeedCheckMinutes": 30
  }
}
```

This controls how often the service checks all subscribed feeds (default: 30 minutes).

## Notification Format

When a new article is published, you'll receive a Telegram message with:

```
📰 Feed Name

Article Title

Description preview (up to 200 characters)...

🔗 Read more: [link]

🕒 Publication date
```

The message uses Markdown formatting with clickable links.

## Technical Details

### Database Schema

**RssFeedSubscription Table:**
- `Id` - Unique identifier
- `FeedUrl` - RSS feed URL
- `FeedName` - User-friendly name
- `SubscribedDate` - When the subscription was created
- `LastChecked` - Last time this feed was checked
- `IsActive` - Whether the subscription is active

**ProcessedFeedItem Table:**
- `Id` - Unique identifier
- `SubscriptionId` - Reference to the subscription
- `ItemGuid` - Unique identifier from the RSS feed
- `Title` - Article title
- `ProcessedDate` - When the article was processed

### Service Architecture

- **NewsRssFeedService.cs** - Background service that polls feeds
- **MediaDatabase.cs** - SQLite collections for subscriptions and processed items
- **TelegramBotService.cs** - Command handlers for subscribe/unsubscribe/feeds

### Features

- **Duplicate Prevention** - Each article is only notified once using GUID tracking
- **HTML Stripping** - Descriptions are cleaned of HTML tags for better readability
- **URL Validation** - Invalid feed URLs are rejected
- **Reactivation** - Unsubscribing marks feeds as inactive; re-subscribing reactivates them
- **Namespace Support** - Handles both default namespace and non-namespaced RSS/Atom feeds

## Example Feeds

Here are some popular news RSS feeds you can subscribe to:

```
# Tech News
/subscribe https://techcrunch.com/feed/ TechCrunch
/subscribe https://www.theverge.com/rss/index.xml The Verge
/subscribe https://news.ycombinator.com/rss Hacker News

# General News
/subscribe https://www.adaderana.lk/rss.php Ada Derana
/subscribe https://rss.nytimes.com/services/xml/rss/nyt/World.xml NY Times World

# Industry Specific
/subscribe https://semiwiki.com/feed/ SemiWiki
/subscribe https://www.anandtech.com/rss/ AnandTech
```

## Troubleshooting

### Not receiving notifications? Follow these steps:

**Step 1: Run diagnostics**
```
/checkfeeds
```

This command will:
- Show all your subscriptions
- Test each feed URL
- Display the latest article and whether it's already been sent
- Identify any connection issues

**Step 2: Check your subscriptions**
```
/feeds
```

Verify that:
- Your feed is listed
- It shows as active (✅)
- The last check time is recent

**Step 3: Common issues and solutions**

| Problem | Solution |
|---------|----------|
| No subscriptions shown | Use `/subscribe <url> <name>` to add feeds |
| Feed marked inactive (❌) | Re-subscribe with the same URL and a new name |
| "Already sent ✅" on all items | Wait for new articles to be published, or all items have been sent |
| Feed test shows error | The feed URL might be invalid or the site is down |
| Last checked shows "Never" | Service might not have started. Wait 30 seconds after app launch |
| Last checked is old | Check application logs for errors in NewsRssFeedService |

**Step 4: Check service logs**

Look for these log entries:
```
📰 News RSS feed monitor started
🚀 News RSS feed monitor started
📡 Checking N active feed subscription(s)
```

If you see errors like:
- `❌ News RSS feed HTTP error` - The feed URL is unreachable
- `❌ Failed to parse XML` - The feed format is invalid
- `❌ Failed to send Telegram notification` - Telegram API issue

**Step 5: Verify configuration**

Check `appsettings.json`:
```json
{
  "MediaBox": {
    "RssFeedCheckMinutes": 30
  }
}
```

The service checks feeds every `RssFeedCheckMinutes` (default: 30 minutes).

### Feed not working

1. Verify the feed URL is valid by opening it in a browser
2. Check that it returns valid XML/RSS content
3. Use `/checkfeeds` to test the feed
4. Try a different feed to see if the issue is service-wide or feed-specific

### Duplicate notifications

This shouldn't happen as the system tracks processed items by GUID. If it does:
1. Check if the feed is providing consistent GUIDs
2. Review the `ProcessedFeedItems` table in the database
3. Report it as a bug with the feed URL

## Future Enhancements

Possible improvements for this feature:
- Feed update frequency per subscription
- Keyword filters (only notify for certain topics)
- Custom notification templates
- RSS feed categories/tags
- OPML import/export for bulk subscription management
- Web UI for managing subscriptions
