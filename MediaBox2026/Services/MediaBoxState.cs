namespace MediaBox2026.Services;

public class MediaBoxState
{
    public event Action? OnChange;

    public int TvShowCount { get; set; }
    public int MovieCount { get; set; }
    public int WatchlistCount { get; set; }
    public int ActiveDownloads { get; set; }
    public int YouTubeCount { get; set; }
    public DateTime? LastMediaScan { get; set; }
    public DateTime? LastRssCheck { get; set; }
    public DateTime? LastNewsDownload { get; set; }
    public List<string> RecentActivity { get; set; } = [];

    /// <summary>Temporary pause set via Telegram. Resets when the app restarts. Takes priority over the persistent setting.</summary>
    public bool YouTubeTemporarilyPaused { get; set; } = false;

    /// <summary>Per-source temporary pauses set via Telegram. Keyed by MatchTitle. Resets on restart.</summary>
    private readonly HashSet<string> _temporarilyPausedSources = new(StringComparer.OrdinalIgnoreCase);

    public bool IsSourceTemporarilyPaused(string matchTitle) => _temporarilyPausedSources.Contains(matchTitle);

    public void PauseSource(string matchTitle)
    {
        _temporarilyPausedSources.Add(matchTitle);
        NotifyChange();
    }

    public void ResumeSource(string matchTitle)
    {
        _temporarilyPausedSources.Remove(matchTitle);
        NotifyChange();
    }

    public IReadOnlyCollection<string> TemporarilyPausedSources => _temporarilyPausedSources;

    private readonly TaskCompletionSource _telegramReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitForTelegramReadyAsync(CancellationToken ct) => _telegramReady.Task.WaitAsync(ct);

    public void SignalTelegramReady() => _telegramReady.TrySetResult();

    public void AddActivity(string message)
    {
        RecentActivity.Insert(0, $"[{DateTime.Now:HH:mm}] {message}");
        if (RecentActivity.Count > 50)
            RecentActivity.RemoveRange(50, RecentActivity.Count - 50);
        NotifyChange();
    }

    public void NotifyChange() => OnChange?.Invoke();
}
