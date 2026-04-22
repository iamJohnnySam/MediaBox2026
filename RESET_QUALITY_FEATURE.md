# Reset Quality Notifications Feature

## 📱 New Telegram Command: `/resetquality`

### Overview
This command allows you to reset all pending high-quality download notifications and trigger a fresh RSS scan to resend them.

### Use Cases

1. **Clear Old Notifications**
   - You have old pending notifications (from days/weeks ago)
   - You want to start fresh with current RSS feed items

2. **Fix Stuck Notifications**
   - Notifications aren't appearing as expected
   - System was down during notification period
   - Want to force a rescan of high-quality items

3. **After Library Updates**
   - You manually downloaded episodes
   - Library was updated through other means
   - Want system to re-check what's actually needed

### How It Works

When you run `/resetquality`, the system will:

1. **Clear Pending Notifications**
   - Marks all `WaitingForQuality` items as `Rejected`
   - Logs each cleared item for auditing

2. **Clear RSS Processing History**
   - Removes RSS processed markers for those items
   - Allows items to be re-evaluated on next scan

3. **Trigger Fresh RSS Scan**
   - Immediately fetches latest RSS feed
   - Re-processes all items
   - Creates new pending notifications for high-quality items (1080p, 2160p, etc.)

4. **Smart Filtering**
   - Won't create notifications for episodes already in your library
   - Won't create notifications for already dispatched episodes
   - Respects the quality wait period (default: 4 hours from RSS publish time)

### Usage

Simply send this command in your Telegram chat with the MediaBox bot:

```
/resetquality
```

### Expected Response

```
🔄 Resetting quality download notifications...

This will:
1. Clear all pending quality requests
2. Re-scan RSS feed
3. Send fresh notifications for high-quality items

Please wait...

✅ Cleared 7 pending notification(s)

🔍 Triggering RSS feed scan...

✅ RSS scan complete!

New quality notifications will be sent after the wait period 
(if any high-quality items are found).
```

### Logs Generated

In your application logs, you'll see:

```
[INF] Reset pending quality item: Euphoria S03E01 MULTi 1080p...
[INF] Reset pending quality item: Daredevil Born Again S02E05...
[INF] Reset 7 pending quality downloads and 12 RSS items
[INF] 🔄 Manual RSS feed check triggered
[INF] RSS feed returned 150 items
[INF] Processing TV show: Euphoria S03E02 (S3E2)
[INF] Quality detected: 1080p (Acceptable: False)
[INF] ⏳ Quality too high (1080p), added to pending downloads: Euphoria S03E02...
[INF] ✅ Manual RSS feed check completed
```

### Important Notes

⏰ **Wait Period Still Applies**
- New pending notifications won't be sent immediately
- System waits for the configured `QualityWaitHours` (default: 4 hours)
- This is calculated from the RSS publish date, not the scan time

✅ **Smart Deduplication**
- Before sending notifications, system checks:
  - If episode exists in your media library
  - If episode was already dispatched to Transmission
- Automatically skips episodes you already have

🔄 **Re-notifications**
- If notifications are sent but you don't respond
- System will re-ask after 24 hours
- This allows you to review if you missed the message

### Example Scenario

**Before Reset:**
```
[INF] Checking 7 pending download(s) for quality approval
[INF] 🔄 Already asked recently for Euphoria S03E01, will retry in 23.5h
[INF] 🔄 Already asked recently for Daredevil S02E05, will retry in 23.5h
...
[INF] CheckPendingQuality complete: 0 notifications sent, 0 still waiting, 7 recently asked
```

**After `/resetquality`:**
```
[INF] Reset 7 pending quality downloads and 12 RSS items
[INF] 🔄 Manual RSS feed check triggered
[INF] RSS feed returned 150 items
[INF] ✅ Episode already in library, removing from pending: Euphoria S03E01
[INF] ⏳ Quality too high (1080p), added to pending downloads: Euphoria S03E02
[INF] Will ask user about this download after 4h wait period
```

### Command Added to Help

The command is now included in `/help`:

```
📋 Commands:
/status - System status
/downloads - Active downloads
/watchlist - Movie watchlist
/movie Movie Name - Search & add movie
/add Movie Name - Quick add to watchlist
/remove Movie Name - Remove from watchlist
/scan - Trigger media scan
/feeds - List RSS subscriptions
/subscribe <url> <name> - Subscribe to RSS feed
/unsubscribe <name> - Unsubscribe from feed
/resetquality - Reset & rescan quality notifications  ← NEW!
/help - Show this message
```

### Technical Details

**Service Architecture:**
- `RssFeedMonitorService` now has a public `TriggerCheckAsync()` method
- Service is registered as a singleton (like `TelegramBotService`)
- Can be retrieved from DI container for manual triggering

**Database Changes:**
- Pending downloads marked as `Rejected` status
- Processed RSS items deleted for re-scanning
- No permanent data loss - items can be re-created

**Logging:**
- All actions logged with structured logging
- Clear emoji indicators (🔄, ✅, ❌)
- Full audit trail of what was reset

### Error Handling

If an error occurs during reset:

```
❌ Error resetting notifications: [error message]
```

Logs will show:
```
[ERR] Error resetting quality notifications
System.Exception: [detailed error information]
```

### When to Use

✅ **Good Use Cases:**
- Weekly cleanup of old notifications
- After manually downloading episodes
- When testing notification system
- After fixing library organization

❌ **Avoid Using:**
- While download operations are in progress
- If you're waiting for a specific notification (just wait 24h for retry)
- As a frequent operation (better to let automatic system work)

### Configuration

No additional configuration required! The command uses your existing settings:

- `QualityWaitHours` - Wait time before sending notifications
- `RssFeedUrl` - RSS feed to scan
- `RssFeedCheckMinutes` - Normal scan interval (bypassed by manual trigger)

---

**This feature gives you full control over quality notifications while maintaining the automatic system's intelligence!** 🎯
