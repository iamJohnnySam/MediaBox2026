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
