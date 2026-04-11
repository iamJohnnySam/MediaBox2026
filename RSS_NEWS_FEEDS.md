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

### Feed not working

1. Verify the feed URL is valid by opening it in a browser
2. Check logs for errors: Look for "NewsRssFeedService" entries
3. Ensure the feed returns valid XML

### Not receiving notifications

1. Make sure you're authenticated with the Telegram bot
2. Check that the feed is marked as active with `/feeds`
3. Verify `RssFeedCheckMinutes` is set reasonably (5-30 minutes)
4. Look for errors in the application logs

### Duplicate notifications

This shouldn't happen as the system tracks processed items by GUID. If it does:
1. Check if the feed is providing consistent GUIDs
2. Review the `ProcessedFeedItems` table in the database

## Future Enhancements

Possible improvements for this feature:
- Feed update frequency per subscription
- Keyword filters (only notify for certain topics)
- Custom notification templates
- RSS feed categories/tags
- OPML import/export for bulk subscription management
- Web UI for managing subscriptions
