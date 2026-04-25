# RSS News Feed Troubleshooting Guide

## You subscribed but aren't receiving messages?

### Quick Diagnostic Steps

1. **Wait for the next check cycle**
   - The service checks feeds every 30 minutes (configured in `appsettings.json`)
   - After subscribing, you may need to wait up to 30 minutes for the first check

2. **Run the diagnostic command in Telegram**
   ```
   /checkfeeds
   ```

   This will tell you:
   - ✅ If your subscription is active
   - 📡 If the feed URL is reachable
   - 🆕 If there are new items to be sent
   - ✅ If items have already been sent

3. **Check your subscription status**
   ```
   /feeds
   ```

   Look for:
   - Is your feed listed?
   - Is it marked as active?
   - When was it last checked?

### Common Issues

#### Issue 1: "Already sent ✅" on all items
**Why this happens:** When you first subscribe, the service will send ALL items currently in the feed on the next check cycle. If you see "Already sent" on all items, it means:
- The service already checked and sent those notifications, OR
- You previously subscribed to this feed and the items are in the database

**Solution:** 
- Wait for new articles to be published by the news source
- Check if you already received the notifications (they might have been sent while you weren't looking)
- If you want to re-receive them, you'd need to clear the database (advanced)

#### Issue 2: Last checked shows "Never"
**Why this happens:** The service hasn't run its first check cycle yet.

**Solution:**
- Wait 30 seconds after app startup (the service has a built-in startup delay)
- Then wait for the next check cycle (configured interval)
- Check application logs for "NewsRssFeedService" entries

#### Issue 3: Feed test shows error
**Why this happens:** The feed URL is invalid or unreachable.

**Solution:**
- Open the feed URL in your browser to verify it works
- Check for typos in the URL
- Some feeds might require special headers or authentication

#### Issue 4: No subscriptions found
**Why this happens:** The `/subscribe` command failed or wasn't processed.

**Solution:**
- Try subscribing again: `/subscribe <url> <name>`
- Check for success message: "✅ Subscribed to: [name]"
- Verify the URL format is correct (must start with http:// or https://)

### Understanding the Service Flow

```
1. You subscribe via Telegram
   ↓
2. Subscription saved to database
   ↓
3. Service checks every 30 minutes (configurable)
   ↓
4. For each active subscription:
   - Fetches the RSS feed
   - Parses items
   - Checks if item already processed
   - Sends new items to Telegram
   - Marks items as processed
```

### What happens when you first subscribe?

When you subscribe to a new feed:
1. The subscription is immediately saved as "active"
2. On the NEXT check cycle (within 30 minutes), the service will:
   - Fetch all items from the feed
   - Send ALL items as notifications (since none are marked as processed yet)
   - Mark all items as processed
3. From then on, only NEW articles will be sent

⚠️ **Important:** This means when you first subscribe, you might receive many notifications at once (depending on how many items are in the feed). This is by design to ensure you don't miss any articles.

### Checking the Logs

Look for these log messages (in Console or log files):

**Good signs:**
```
📰 News RSS feed monitor started
🚀 News RSS feed monitor started
📡 Checking N active feed subscription(s)
✅ News RSS feed check cycle completed
✅ Feed 'Feed Name': X new item(s) processed
```

**Problems:**
```
❌ News RSS feed HTTP error - Feed URL is unreachable
❌ Failed to parse XML - Feed format is invalid
❌ Failed to send Telegram notification - Telegram API issue
No active RSS feed subscriptions found - No subscriptions or all inactive
```

### Testing with a Known-Good Feed

Try subscribing to a test feed that updates frequently:

```
/subscribe https://hnrss.org/newest Hacker News Latest
```

This feed updates multiple times per hour, so you should receive notifications within 30 minutes of subscribing.

### Still Not Working?

1. Check if the NewsRssFeedService is running:
   - Look for startup logs: "📰 News RSS feed monitor started"
   - If not, check Program.cs for service registration

2. Check application errors:
   - Review log files in the Logs/ directory
   - Look for exceptions in NewsRssFeedService

3. Verify Telegram connectivity:
   - Test with other commands like `/status`
   - Check if you're authenticated with the bot

4. Check configuration:
   - Verify `RssFeedCheckMinutes` in appsettings.json
   - Ensure it's not set to an extremely long interval

### Advanced: Manual Database Check

If you have SQLite tools, you can check the database directly:

```sql
-- Check subscriptions
SELECT * FROM RssFeedSubscription;

-- Check processed items
SELECT * FROM ProcessedFeedItem;

-- Clear processed items for a subscription (to re-receive notifications)
DELETE FROM ProcessedFeedItem WHERE SubscriptionId = [your_subscription_id];
```

Database location: `MediaBox2026/data/mediabox.db`
