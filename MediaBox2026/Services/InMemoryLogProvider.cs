using System.Collections.Concurrent;
using MediaBox2026.Models;

namespace MediaBox2026.Services;

public class InMemoryLogSink
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private const int MaxEntries = 1000;

    public event Action? OnNewLog;
    public event Action<LogEntry>? OnErrorLog;

    public IReadOnlyList<LogEntry> GetEntries() => [.. _entries];

    public void Add(LogEntry entry)
    {
        _entries.Enqueue(entry);
        while (_entries.Count > MaxEntries)
            _entries.TryDequeue(out _);

        OnNewLog?.Invoke();

        if (entry.Level >= LogLevel.Error)
            OnErrorLog?.Invoke(entry);
    }
}

public class InMemoryLoggerProvider(InMemoryLogSink sink) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new InMemoryLogger(categoryName, sink);

    public void Dispose() { }
}

public class InMemoryLogger(string category, InMemoryLogSink sink) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        sink.Add(new LogEntry
        {
            Timestamp = DateTime.Now,
            Level = logLevel,
            Category = category,
            Message = formatter(state, exception),
            Exception = exception?.ToString()
        });
    }
}
