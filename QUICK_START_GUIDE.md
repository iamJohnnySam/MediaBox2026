# MediaBox2026 - Quick Start Guide for Improvements

## 🚀 What Changed?

Your MediaBox2026 project has been comprehensively improved with enhanced reliability, observability, and maintainability. This guide helps you understand what's new and how to use it.

---

## ✨ Key Improvements at a Glance

### 1. **Self-Healing Services**
- All background services now automatically retry failed operations
- Failures are tracked and logged clearly
- Services increase retry delays when problems persist
- Services continue running even after temporary failures

### 2. **Better Observability**
- Every operation logs its duration
- Emoji indicators for quick log scanning (🚀 ✅ ❌ ⚠️)
- Detailed error messages with full context
- Clear service lifecycle events (starting, stopping, cycling)

### 3. **Enhanced Reliability**
- HTTP clients have timeouts (no more hanging forever)
- Process execution has timeouts (yt-dlp won't hang)
- Proper cancellation handling (clean shutdowns)
- Path validation (auto-creates missing directories)

### 4. **Improved Debugging**
- Performance metrics show bottlenecks
- Failure tracking shows problem patterns
- Detailed API logging helps diagnose issues
- Structured log format easy to parse

---

## 📖 Reading the Logs

### Emoji Guide

| Emoji | Meaning | Example |
|-------|---------|---------|
| 🚀 | Service starting | `🚀 News RSS feed monitor started` |
| ✅ | Success | `✅ Cycle completed in 2.3s` |
| ❌ | Error | `❌ HTTP error (failures: 3/5)` |
| ⚠️ | Warning | `⚠️ Consecutive failures: 2/5` |
| 🛑 | Shutdown | `🛑 Service shutting down...` |
| 📡 | Network | `📡 Checking 3 feed subscriptions` |
| 📊 | Statistics | `📊 Summary: 9 processed, 3 skipped` |
| 🔍 | Search | `🔍 Checking 4 watchlist items` |
| 📥 | Download | `📥 Starting YouTube download` |
| 📺 | TV Show | `📺 Organized: Show S01E05` |
| 🎬 | Movie | `🎬 Organized: Movie Name` |
| ⏸️ | Pause | `⏸️ Paused large torrent` |

### Log Level Guide

**[INF] Information**
```
[INF] 🚀 News RSS feed monitor started
[INF] Check interval: 30 minutes
[INF] ✅ Cycle completed in 2.3s
```
👉 Normal operation, nothing to worry about

**[WRN] Warning**
```
[WRN] ⚠️ Consecutive failures: 3/5
[WRN] ⚠️ Downloads path does not exist: /path/to/downloads
```
👉 Something is wrong but not critical, service will retry

**[ERR] Error**
```
[ERR] ❌ HTTP error (failures: 4/5): Connection refused
[ERR] ❌ Failed to process: Movie.Name
```
👉 Operation failed, service will continue and retry

**[CRI] Critical**
```
[CRI] 🚨 Max consecutive failures reached
[CRI] ❌ yt-dlp is not installed
```
👉 Serious problem, service may stop or delay significantly

---

## 🔍 Monitoring Your System

### Check Service Health

**View the health endpoint:**
```bash
curl http://localhost:5000/health
```

**Response:**
```json
{
  "status": "healthy",
  "timestamp": "2024-01-15T14:30:00Z",
  "lastRssCheck": "2024-01-15T14:25:00",
  "lastMediaScan": "2024-01-15T10:00:00",
  "services": {
    "database": "connected",
    "rssMonitor": "running",
    "mediaScanner": "running"
  },
  "counts": {
    "tvShows": 150,
    "movies": 320,
    "watchlist": 25,
    "activeDownloads": 3
  }
}
```

### Monitor Logs in Real-Time

**Linux/Mac:**
```bash
tail -f Logs/$(date +%Y)/$(date +%m)/mediabox-*.log
```

**Windows PowerShell:**
```powershell
Get-Content -Path "Logs\$(Get-Date -Format 'yyyy')\$(Get-Date -Format 'MM')\mediabox-*.log" -Wait -Tail 50
```

### Look for Problems

**Find errors:**
```bash
grep "ERR\|CRI" Logs/$(date +%Y)/$(date +%m)/mediabox-*.log
```

**Find slow operations:**
```bash
grep "completed in [0-9][0-9]\." Logs/$(date +%Y)/$(date +%m)/mediabox-*.log
# Shows operations taking 10+ seconds
```

**Check failure counts:**
```bash
grep "Consecutive failures" Logs/$(date +%Y)/$(date +%m)/mediabox-*.log
```

---

## 🛠️ Troubleshooting Guide

### Problem: Service keeps retrying with failures

**Symptoms:**
```
[WRN] ⚠️ Consecutive failures: 4/5
[ERR] ❌ HTTP error (failures: 5/5)
[CRI] 🚨 Max consecutive failures reached
```

**Solutions:**
1. Check network connectivity
2. Verify external service is online (YTS, TVMaze, etc.)
3. Check API keys are still valid
4. Look for rate limiting from external services
5. Check firewall/proxy settings

**What happens:**
- After 5 consecutive failures, service extends retry delay (2x normal)
- Service resets failure counter after extended delay
- Service continues trying indefinitely

### Problem: YouTube downloads timing out

**Symptoms:**
```
[ERR] ⏱️ yt-dlp process timed out after 30 minutes
```

**Solutions:**
1. Check internet speed (might be slow download)
2. Verify YouTube URL is still valid
3. Check yt-dlp is up to date: `yt-dlp --update`
4. Try downloading manually to test
5. Check disk space

**Configuration:**
- Default timeout: 30 minutes
- Configurable in `YouTubeDownloadService.cs` if needed

### Problem: Files not being organized

**Symptoms:**
```
[WRN] ⚠️ Downloads path does not exist: /path/to/downloads
[ERR] ❌ Failed to process: Movie.Name
```

**Solutions:**
1. Verify paths in `appsettings.json` are correct
2. Check permissions on directories
3. Ensure no active torrents (files are skipped if torrent is active)
4. Check Transmission is accessible
5. Look for disk space issues

**Auto-fixes:**
- Service auto-creates TV Shows and Movies paths if missing
- Service skips active torrents automatically
- Service continues after individual file failures

### Problem: Watchlist not finding movies

**Symptoms:**
```
[WRN] YTS API returned 429 for: Movie Name
[DBG] No results found for: Movie Name
```

**Solutions:**
1. Check YTS.bz is online and accessible
2. Verify not rate limited (max 1 call/second)
3. Try searching on YTS.bz website manually
4. Check movie name spelling
5. Add year to watchlist item for better matching

**Rate Limiting:**
- Service respects 1 second between API calls
- Prevents rate limiting from YTS

### Problem: Large torrents not asking for approval

**Symptoms:**
- Large torrents (>1GB) auto-downloading without approval

**What to check:**
1. Check Telegram bot is connected
2. Verify you responded to previous approvals
3. Check PendingLargeTorrents table in database
4. Look for Telegram delivery failures

**How it works:**
- Torrent >1GB from RSS is auto-paused
- Telegram message sent with approval buttons
- Torrent only resumes after approval
- 24-hour timeout on approval requests

---

## 📊 Performance Expectations

### Normal Operation Times

| Operation | Expected Duration | Warning Threshold |
|-----------|------------------|-------------------|
| RSS Feed Check | 1-5 seconds | > 10 seconds |
| Transmission Monitor | 0.5-2 seconds | > 5 seconds |
| Download Organization | 2-10 seconds | > 30 seconds |
| Watchlist Check | 5-15 seconds | > 30 seconds |
| YouTube Download | 30 seconds - 10 minutes | > 30 minutes |
| Media Scan | 10-30 seconds | > 60 seconds |

### If Operations Are Slow

**Possible causes:**
1. **Network issues** - Check internet speed
2. **Disk I/O** - Check disk health, maybe SSD is slow
3. **Large library** - Media scan scales with library size
4. **External API slow** - YTS or TVMaze might be slow
5. **System resources** - Check CPU/memory usage

**Solutions:**
- Increase check intervals in `appsettings.json`
- Optimize database (vacuum, reindex)
- Consider faster storage (SSD)
- Split large operations into smaller batches

---

## 🎛️ Configuration Tips

### Tuning Check Intervals

**In `appsettings.json`:**
```json
{
  "MediaBox": {
    "RssFeedCheckMinutes": 30,        // News RSS feeds
    "TransmissionCheckMinutes": 5,     // Torrent monitoring
    "DownloadOrganizerMinutes": 10,    // File organization
    "WatchlistCheckHours": 6,          // Movie watchlist
    "MediaScanHours": 12               // Media library scan
  }
}
```

**Recommendations:**
- **Slow network**: Increase all intervals by 50%
- **Fast network + many items**: Decrease intervals by 50%
- **Limited API calls**: Increase `WatchlistCheckHours` to 12-24
- **Active downloading**: Decrease `TransmissionCheckMinutes` to 2-3
- **Large library**: Increase `MediaScanHours` to 24

### Path Configuration

**Critical paths to configure:**
```json
{
  "TvShowsPath": "/molecule/Media/TVShows",
  "MoviesPath": "/molecule/Media/Movies",
  "DownloadsPath": "/molecule/Media/Downloads",
  "YouTubePath": "/molecule/Media/YouTube",
  "UnknownPath": "/molecule/Media/Unknown",
  "DatabasePath": "data/mediabox.db"
}
```

**Best practices:**
- Use absolute paths
- Ensure write permissions
- Separate downloads from organized media
- Regular backups of `DatabasePath`

---

## 📈 Measuring Success

### Before Improvements

**Typical issues:**
- Services failing silently
- No way to know what's happening
- Troubleshooting takes hours
- Unclear why operations fail
- Services need manual restarts

### After Improvements

**Expected experience:**
- Services recover automatically
- Clear logs show exactly what's happening
- Troubleshooting takes minutes
- Errors explain why and suggest fixes
- Services run indefinitely without intervention

### Success Indicators

✅ **Logs show emoji indicators throughout**
✅ **Cycle completion times are consistent**
✅ **Consecutive failures rarely exceed 2**
✅ **Services run for weeks without intervention**
✅ **Issues are identified in minutes not hours**

---

## 🆘 Getting Help

### Information to Provide

When reporting issues, include:

1. **Log excerpt** showing the problem:
   ```
   [ERR] ❌ HTTP error (failures: 3/5): Connection refused
   ```

2. **Service affected**:
   - News RSS Feed Service
   - Transmission Monitor
   - etc.

3. **Configuration** (sanitize secrets):
   ```json
   {
     "RssFeedCheckMinutes": 30,
     "TransmissionRpcUrl": "http://localhost:9091/..."
   }
   ```

4. **System info**:
   - OS (Windows/Linux/Mac)
   - .NET version
   - MediaBox2026 version

5. **What you tried**:
   - Restarted service?
   - Checked network?
   - Verified paths?

### Where to Get Help

- **GitHub Issues**: https://github.com/iamJohnnySam/MediaBox2026/issues
- **Documentation**: Check PROJECT_AUDIT_AND_IMPROVEMENTS.md
- **Logs**: Most issues explained in log messages

---

## 🎓 Learning More

### Documents to Read

1. **PROJECT_AUDIT_AND_IMPROVEMENTS.md**
   - Complete project overview
   - Architecture explanation
   - Future improvements planned

2. **IMPROVEMENTS_IMPLEMENTATION_SUMMARY.md**
   - What changed and why
   - Before/after comparisons
   - Technical details

3. **CODE_IMPROVEMENTS.md**
   - All improvements made
   - Validation features
   - Health checks

### Understanding the Code

**Standard service pattern:**
```csharp
protected override async Task ExecuteAsync(CancellationToken ct)
{
    // 1. Wait for dependencies
    await state.WaitForTelegramReadyAsync(ct);

    // 2. Startup tasks
    await Task.Delay(DelayToPreventOverlap, ct);

    // 3. Main loop
    while (!ct.IsCancellationRequested)
    {
        try {
            // Do work
            // Track failures
            // Log metrics
        }
        catch {
            // Handle errors
            // Categorize exceptions
            // Update failure tracking
        }

        // Wait for next cycle
        await Task.Delay(CheckInterval, ct);
    }
}
```

---

## ✅ Quick Checklist

After upgrading to improved version:

- [ ] Review new log format and emojis
- [ ] Check health endpoint works: `curl http://localhost:5000/health`
- [ ] Monitor logs for first hour to see improvements
- [ ] Verify all services are starting successfully
- [ ] Check no new errors introduced
- [ ] Update monitoring/alerting for new log format
- [ ] Read PROJECT_AUDIT_AND_IMPROVEMENTS.md
- [ ] Consider next phase improvements

---

## 🎉 You're Ready!

Your MediaBox2026 is now **production-ready** with:
- ✅ Self-healing services
- ✅ Comprehensive logging
- ✅ Performance metrics
- ✅ Better error handling
- ✅ Improved reliability

**Enjoy your improved media management system!** 🚀

---

**Questions?** Check the documentation or create a GitHub issue.

**Found a bug?** The detailed logs will help us fix it quickly!

**Want to contribute?** See PROJECT_AUDIT_AND_IMPROVEMENTS.md for next steps.
