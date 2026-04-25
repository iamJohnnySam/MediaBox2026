# RSS News Feed Service - Implementation Summary

## What Was Done

I've added diagnostic and troubleshooting capabilities to your RSS news feed service. The service itself was already correctly implemented, but there were no tools to diagnose why notifications weren't being received.

## Changes Made

### 1. New Telegram Commands

#### `/checkfeeds` (or `/diagnose`)
Runs comprehensive diagnostics on all your RSS feed subscriptions:
- Shows total and active subscription counts
- Tests each feed URL for connectivity
- Displays the latest article from each feed
- Indicates whether articles have been sent or are new
- Shows error messages if feeds are unreachable

**Usage:**
```
/checkfeeds
```

**Example Output:**
```
📰 RSS News Feed Diagnostics

Total subscriptions: 1
Active: 1

• Ada Derana News
  Active: ✅
  Last checked: 1/15/2024 2:30 PM
  Processed items: 25
  Feed test: ✅ 50 items
  Latest: Breaking: Major news headline here...
  Status: New (will be sent) 🆕
```

#### `/checknews`
Quick info about the news feed checking interval and points to diagnostics.

**Usage:**
```
/checknews
```

### 2. Updated Help Command

The `/help` command now includes:
```
/checkfeeds - Test RSS news feeds
```

### 3. Documentation

Created two comprehensive guides:

- **RSS_NEWS_TROUBLESHOOTING.md** - Detailed troubleshooting guide
- Updated **RSS_NEWS_FEEDS.md** - Enhanced with troubleshooting section

### 4. Diagnostic Utility

Created `DiagnosticNewsFeed.cs` - A standalone diagnostic class that can be used programmatically if needed.

## How the RSS News Service Works

### Service Architecture

```
NewsRssFeedService (Background Service)
    ↓
Checks every 30 minutes (configurable via RssFeedCheckMinutes)
    ↓
For each active subscription:
    1. Fetches RSS feed via HTTP
    2. Parses XML (supports RSS and Atom formats)
    3. Extracts: title, description, link, pubDate, guid
    4. Checks if item already processed (via ProcessedFeedItems table)
    5. Sends NEW items to Telegram with formatted message
    6. Marks items as processed in database
```

### Database Schema

**RssFeedSubscription:**
- Stores feed URL, name, subscription date, last check time, active status

**ProcessedFeedItem:**
- Tracks which articles have been sent to prevent duplicates
- Uses GUID from RSS feed as unique identifier

## Most Likely Reasons for Not Receiving Notifications

### 1. **Service hasn't run yet** (Most Common)
- The service checks every 30 minutes
- It also waits 30 seconds after startup before first check
- **Solution:** Wait up to 30 minutes after subscribing

### 2. **All items already processed**
- If you subscribed, unsubscribed, and re-subscribed
- The items are still marked as processed in the database
- **Solution:** Wait for new articles, or use `/checkfeeds` to verify

### 3. **Feed has no new items**
- The news source hasn't published anything new
- **Solution:** Use `/checkfeeds` to see the latest article date

### 4. **Feed URL is invalid or unreachable**
- Network issues, wrong URL, or site is down
- **Solution:** Use `/checkfeeds` to test connectivity

## Next Steps for You

1. **Run the diagnostic command:**
   ```
   /checkfeeds
   ```
   This will immediately tell you what's happening with your subscription.

2. **Check the output carefully:**
   - If it says "New (will be sent) 🆕" → The next check cycle will send it
   - If it says "Already sent ✅" → You already received those notifications
   - If it shows an error → The feed URL has issues

3. **Wait for the next check cycle:**
   - Default is 30 minutes
   - Check your logs for: "📰 News RSS feed monitor started"

4. **Verify you're receiving notifications:**
   - Test with `/status` to ensure Telegram is working
   - Check if notifications are being sent but you're missing them

## Configuration

In `appsettings.json`:
```json
{
  "MediaBox": {
    "RssFeedCheckMinutes": 30  // Change this to check more/less frequently
  }
}
```

## Example: Ada Derana Feed

For the feed you mentioned: `https://www.adaderana.lk/rss.php`

```
/subscribe https://www.adaderana.lk/rss.php Ada Derana News
```

Then run:
```
/checkfeeds
```

You should see:
- If the feed is reachable
- How many items it contains
- Whether any are new or already sent

## Logs to Watch

Look for these in your application logs:

```
📰 News RSS feed monitor waiting for Telegram readiness...
🚀 News RSS feed monitor started
=== News RSS Feed Check Cycle Starting ===
📡 Checking 1 active feed subscription(s)
✅ Feed 'Ada Derana News': 5 new item(s) processed
✅ News RSS feed check cycle completed
```

If you see errors:
```
❌ News RSS feed HTTP error
❌ Failed to parse XML
❌ Failed to send Telegram notification
```

These indicate specific problems that need investigation.

## Code Files Modified

1. **MediaBox2026\Services\TelegramBotService.cs**
   - Added `/checkfeeds` command handler
   - Added `/checknews` command handler
   - Updated help text

2. **MediaBox2026\DiagnosticNewsFeed.cs** (NEW)
   - Standalone diagnostic utility

3. **RSS_NEWS_FEEDS.md**
   - Added diagnostic command documentation
   - Enhanced troubleshooting section

4. **RSS_NEWS_TROUBLESHOOTING.md** (NEW)
   - Comprehensive troubleshooting guide

## No Changes to Core Service

The `NewsRssFeedService.cs` itself was **not modified** because it was already correctly implemented. The issue is likely timing or database state, which can now be diagnosed with `/checkfeeds`.
