using System.Collections.Concurrent;
using MediaBox2026.Models;

namespace MediaBox2026.Services;

/// <summary>One button on an open prompt: what it says, and the callback value it answers with.</summary>
public record PromptChoice(string Label, string Value);

/// <summary>A question MediaBox has asked and is still waiting on an answer to.</summary>
public record PendingPrompt(string Id, string Text, List<PromptChoice> Options, DateTime AskedAt);

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

    /// <summary>
    /// The inline-keyboard prompts still waiting on an answer (large torrent, quality wait,
    /// watchlist confirmation, planned-download day), keyed by the callback id their buttons carry.
    /// Recorded by whichever ITelegramNotifier sent the keyboard, so Tower's gRPC can answer exactly
    /// what Telegram is being asked — the answer goes into the same TaskCompletionSource either way.
    ///
    /// In memory on purpose: so is the TaskCompletionSource that acts on the answer, so a restart
    /// kills a prompt on both channels alike and MediaBox re-asks. Pruned on read against
    /// PendingCallbacks, which is the one source of truth for "still answerable".
    /// </summary>
    public ConcurrentDictionary<string, PendingPrompt> OpenPrompts { get; } = new();

    /// <summary>
    /// Records a keyboard as an open prompt. Every prompt's buttons carry "{callbackId}:{value}";
    /// anything else (the /movie search session's "ms:" buttons) is not an approval and is ignored.
    /// </summary>
    public void RecordPrompt(string text, List<List<InlineButton>> buttons)
    {
        var choices = buttons.SelectMany(row => row)
            .Select(b => (b.Text, Parts: b.CallbackData.Split(':', 2)))
            .Where(x => x.Parts.Length == 2 && x.Parts[0].Length > 0 && x.Parts[0] != "ms")
            .ToList();
        if (choices.Count == 0) return;

        var id = choices[0].Parts[0];
        OpenPrompts[id] = new PendingPrompt(id, text,
            choices.Select(c => new PromptChoice(c.Text, c.Parts[1])).ToList(), DateTime.UtcNow);
    }

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
