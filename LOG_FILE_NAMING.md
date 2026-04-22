# Log File Naming - Instance-Based Logging

## Overview
Each time MediaBox2026 starts, it creates a **new log file** with a unique timestamp including milliseconds.

## File Naming Format
```
mediabox-YYYYMMDD-HHmmss-fff.log
```

### Components:
- `YYYYMMDD`: Date (Year-Month-Day) - e.g., `20260411`
- `HHmmss`: Time in 24-hour format (Hour-Minute-Second) - e.g., `143052`
- `fff`: Milliseconds - e.g., `123`

### Example File Names:
```
mediabox-20260411-143052-123.log  (Started: April 11, 2026 at 2:30:52.123 PM)
mediabox-20260411-143052-456.log  (Started: April 11, 2026 at 2:30:52.456 PM)
mediabox-20260412-090030-789.log  (Started: April 12, 2026 at 9:00:30.789 AM)
```

## Folder Structure
Logs are organized by year and month:
```
Logs/
├── 2026/
│   ├── 04/
│   │   ├── mediabox-20260411-143052-123.log
│   │   ├── mediabox-20260411-180445-456.log
│   │   ├── mediabox-20260412-090030-789.log
│   │   └── mediabox-20260412-153020-111.log
│   ├── 05/
│   │   └── mediabox-20260501-080000-000.log
│   └── ...
└── ...
```

## Benefits

### ✅ Unique Per Instance
- Each application start gets its own log file
- No log mixing between different runs
- Easy to trace issues to specific application instances

### ✅ Millisecond Precision
- Prevents conflicts if app is restarted multiple times per second
- Guarantees unique file names even in rapid restart scenarios

### ✅ Chronological Sorting
- File names sort naturally by date and time
- Easy to find logs from specific time periods
- Simple directory listing shows logs in order

### ✅ Multiple Simultaneous Instances
- If you run multiple instances simultaneously (e.g., development + production)
- Each instance gets its own log file
- No file locking or sharing issues

## Log Rotation

### Size-Based Rotation
- If a single log file exceeds **100MB**, it automatically rolls to a new file
- The rolled file gets a sequence number: `mediabox-20260411-143052-123_001.log`

### No Time-Based Rotation
- Unlike daily rotation, logs don't roll at midnight
- Each instance keeps writing to its own file until shutdown or size limit

## Log Cleanup

### Manual Cleanup Recommended
Since there's no automatic retention policy, consider:

1. **PowerShell Script** (Windows):
```powershell
# Delete logs older than 30 days
$logPath = "Logs"
$daysToKeep = 30
Get-ChildItem -Path $logPath -Recurse -Filter "mediabox-*.log" |
    Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-$daysToKeep) } |
    Remove-Item -Force
```

2. **Bash Script** (Linux):
```bash
# Delete logs older than 30 days
find ./Logs -name "mediabox-*.log" -type f -mtime +30 -delete
```

3. **Scheduled Task/Cron Job**:
   - Run cleanup weekly or monthly
   - Keep last N days/weeks based on your needs

## Identifying Logs

### By Date
```powershell
# All logs from April 11, 2026
Get-ChildItem -Path "Logs/2026/04" -Filter "mediabox-20260411-*.log"
```

### By Time Range
```powershell
# All logs from afternoon (12:00 PM - 6:00 PM)
Get-ChildItem -Path "Logs/2026/04" -Filter "mediabox-20260411-1[2-7]*.log"
```

### Latest Log
```powershell
# Most recent log file
Get-ChildItem -Path "Logs" -Recurse -Filter "mediabox-*.log" |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
```

## Migration from Daily Logs

If you previously used daily logs (`mediabox-20260411.log`), you'll now have:
- Multiple log files per day
- More granular tracking
- Easier debugging of specific application runs

### Before (Daily):
```
mediabox-20260411.log  (All activity for April 11)
```

### After (Instance-based):
```
mediabox-20260411-090030-123.log  (Morning run)
mediabox-20260411-140052-456.log  (Afternoon restart)
mediabox-20260411-200015-789.log  (Evening restart)
```

## Best Practices

1. **Monitor Disk Space**: Instance-based logs can accumulate quickly
2. **Implement Cleanup**: Set up automated cleanup for old logs
3. **Archive Important Logs**: Before cleanup, archive logs related to incidents
4. **Log Aggregation**: Consider using log aggregation tools for production
5. **Startup Messages**: Each log starts with startup message showing timestamp
