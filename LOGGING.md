# MediaBox2026 Logging Configuration

## Overview
MediaBox2026 uses a dual logging system:
1. **Serilog File Logging** - Persistent, high-performance file-based logging
2. **InMemory Log Sink** - For the Blazor UI log viewer

## File Logging Details

### Technology
- **Serilog** with async file sink for non-blocking, high-performance logging
- Logs are written to disk asynchronously with 1-second flush intervals

### Log Location
Logs are organized in a clean folder structure:
```
Logs/
├── 2026/
│   ├── 01/
│   │   ├── mediabox-20260101.log
│   │   ├── mediabox-20260102.log
│   │   └── ...
│   ├── 02/
│   │   ├── mediabox-20260201.log
│   │   └── ...
│   └── ...
└── ...
```

### Rotation Policy
- **Daily Rotation**: New log file created each day at midnight
- **Size Limit**: Individual log files are limited to 100MB
- **Retention**: Last 31 days of logs are kept (older logs are automatically deleted)
- **File Naming**: `mediabox-YYYYMMDD.log` (e.g., `mediabox-20260411.log`)

### Log Format
Each log entry contains:
```
2026-04-11 14:30:45.123 +00:00 [INF] MediaBox2026.Services.TelegramBotService: Bot started successfully
```
- Timestamp (with milliseconds and timezone)
- Log Level (INF, WRN, ERR, FTL)
- Source Context (class name)
- Message
- Exception details (if applicable)

### Log Levels
- **Information**: Normal operational messages
- **Warning**: Warning messages for Microsoft.AspNetCore and above
- **Error**: Error conditions
- **Fatal**: Critical failures

## Performance
- **Non-blocking**: File writes happen asynchronously and won't delay application execution
- **Buffered**: Logs are flushed to disk every 1 second
- **Efficient**: Serilog is highly optimized for performance

## Configuration
Logging is configured in `Program.cs`:
- Minimum level: Information
- Microsoft.AspNetCore minimum level: Warning
- Console output: Enabled (for development)
- File output: Enabled with daily rotation

## Accessing Logs

### From Filesystem
- **Windows Development**: `<AppDirectory>/Logs/YYYY/MM/mediabox-YYYYMMDD.log`
- **Linux Production**: `<AppDirectory>/Logs/YYYY/MM/mediabox-YYYYMMDD.log`

### From Blazor UI
Use the built-in log viewer in the application (powered by InMemoryLogSink)

## Notes
- The `Logs/` directory is automatically excluded from Git via `.gitignore`
- Logs are created relative to the application binary location (`AppContext.BaseDirectory`)
- On application restart, a new log file is created with the current date
- Old logs are automatically cleaned up after 31 days
