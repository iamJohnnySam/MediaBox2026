using Grpc.Core;
using MediaBox.Control.Grpc;
using MediaBox2026.Models;
using Microsoft.Extensions.Options;

namespace MediaBox2026.Services;

/// <summary>
/// gRPC control server for the Tower -> MediaBox channel (port 5602).
/// Trigger RPCs (this task) call the same entrypoints as the Telegram command handlers
/// in TelegramBotService.HandleCommandAsync, so behavior is identical to the existing
/// /scan, /youtube, /speedmode, etc. commands. Query and mutation RPCs remain
/// Unimplemented stubs (Tasks 4-5).
/// </summary>
public class MediaBoxControlService(
	MediaCatalogService catalog,
	DownloadOrganizerService downloadOrganizer,
	RssFeedMonitorService rssMonitor,
	TransmissionMonitorService transmissionMonitor,
	YouTubeDownloadService youtubeDownload,
	TransmissionClient transmission,
	MediaBoxState state,
	MediaDatabase db,
	IOptionsMonitor<MediaBoxSettings> settings,
	ILogger<MediaBoxControlService> logger) : MediaBoxControl.MediaBoxControlBase
{
	private static RpcException NotYet() =>
		new(new Grpc.Core.Status(StatusCode.Unimplemented, "not yet"));

	// Triggers

	/// <summary>Mirrors the /scan Telegram command (MediaCatalogService.ScanAllAsync).</summary>
	public override async Task<RunResult> Scan(Empty request, ServerCallContext context)
	{
		try
		{
			await catalog.ScanAllAsync(context.CancellationToken);
			return new RunResult { Ok = true, Message = $"Scan complete. TV: {state.TvShowCount}, Movies: {state.MovieCount}" };
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "gRPC Scan trigger failed");
			return new RunResult { Ok = false, Message = ex.Message };
		}
	}

	/// <summary>Mirrors DownloadOrganizerService's per-cycle work (Organize -> RunOnceAsync).</summary>
	public override async Task<RunResult> Organize(Empty request, ServerCallContext context)
	{
		try
		{
			await downloadOrganizer.RunOnceAsync(context.CancellationToken);
			return new RunResult { Ok = true, Message = "Organize cycle complete." };
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "gRPC Organize trigger failed");
			return new RunResult { Ok = false, Message = ex.Message };
		}
	}

	/// <summary>Mirrors RssFeedMonitorService's per-cycle work (RssCheck -> RunOnceAsync).</summary>
	public override async Task<RunResult> RssCheck(Empty request, ServerCallContext context)
	{
		try
		{
			await rssMonitor.RunOnceAsync(context.CancellationToken);
			return new RunResult { Ok = true, Message = "RSS feed check complete." };
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "gRPC RssCheck trigger failed");
			return new RunResult { Ok = false, Message = ex.Message };
		}
	}

	/// <summary>Mirrors TransmissionMonitorService's per-cycle work (TransmissionPoll -> RunOnceAsync).</summary>
	public override async Task<RunResult> TransmissionPoll(Empty request, ServerCallContext context)
	{
		try
		{
			await transmissionMonitor.RunOnceAsync(context.CancellationToken);
			return new RunResult { Ok = true, Message = "Transmission poll complete." };
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "gRPC TransmissionPoll trigger failed");
			return new RunResult { Ok = false, Message = ex.Message };
		}
	}

	/// <summary>
	/// Mirrors the /youtube (and /downloadyt) Telegram command, which calls the bounded
	/// manual-download entrypoint (TriggerManualDownloadAsync) -- NOT RunOnceAsync, which is
	/// the schedule-driven loop body and can block for a long time.
	/// </summary>
	public override async Task<RunResult> YouTubeDownload(Empty request, ServerCallContext context)
	{
		try
		{
			var successCount = await youtubeDownload.TriggerManualDownloadAsync(context.CancellationToken);
			return successCount switch
			{
				-1 => new RunResult { Ok = false, Message = "A YouTube download is already in progress." },
				-2 => new RunResult { Ok = false, Message = "YouTube downloads are temporarily paused." },
				-3 => new RunResult { Ok = false, Message = "YouTube downloads are disabled in settings (YouTubeDownloadPaused=true)." },
				_ => new RunResult { Ok = true, Message = $"YouTube download complete: {successCount} source(s) downloaded." }
			};
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "gRPC YouTubeDownload trigger failed");
			return new RunResult { Ok = false, Message = ex.Message };
		}
	}

	/// <summary>Mirrors the /youtubepause (/ytpause) Telegram command. Empty title = pause all.</summary>
	public override Task<RunResult> YouTubePause(TitleArg request, ServerCallContext context)
	{
		try
		{
			var arg = request.Title?.Trim() ?? "";
			if (string.IsNullOrWhiteSpace(arg))
			{
				state.YouTubeTemporarilyPaused = true;
				state.AddActivity("YouTube downloads temporarily paused via Tower");
				logger.LogInformation("⏸️ YouTube downloads paused via gRPC trigger");
				return Task.FromResult(new RunResult { Ok = true, Message = "All YouTube downloads paused." });
			}

			var matchedSource = settings.CurrentValue.NewsSources
				.FirstOrDefault(s => s.MatchTitle.Contains(arg, StringComparison.OrdinalIgnoreCase));
			if (matchedSource is null)
				return Task.FromResult(new RunResult { Ok = false, Message = $"No source matching \"{arg}\" found." });

			state.PauseSource(matchedSource.MatchTitle);
			state.AddActivity($"YouTube source paused: {matchedSource.MatchTitle}");
			logger.LogInformation("⏸️ YouTube source paused via gRPC trigger: {Title}", matchedSource.MatchTitle);
			return Task.FromResult(new RunResult { Ok = true, Message = $"Paused: {matchedSource.MatchTitle}" });
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "gRPC YouTubePause trigger failed");
			return Task.FromResult(new RunResult { Ok = false, Message = ex.Message });
		}
	}

	/// <summary>Mirrors the /youtuberesume (/ytresume) Telegram command. Empty title = resume all.</summary>
	public override Task<RunResult> YouTubeResume(TitleArg request, ServerCallContext context)
	{
		try
		{
			var arg = request.Title?.Trim() ?? "";
			if (string.IsNullOrWhiteSpace(arg))
			{
				state.YouTubeTemporarilyPaused = false;
				foreach (var src in settings.CurrentValue.NewsSources)
					state.ResumeSource(src.MatchTitle);
				state.AddActivity("YouTube downloads resumed via Tower");
				logger.LogInformation("▶️ YouTube downloads resumed via gRPC trigger");
				return Task.FromResult(new RunResult { Ok = true, Message = "All YouTube downloads resumed." });
			}

			var matchedSource = settings.CurrentValue.NewsSources
				.FirstOrDefault(s => s.MatchTitle.Contains(arg, StringComparison.OrdinalIgnoreCase));
			if (matchedSource is null)
				return Task.FromResult(new RunResult { Ok = false, Message = $"No source matching \"{arg}\" found." });

			state.ResumeSource(matchedSource.MatchTitle);
			state.AddActivity($"YouTube source resumed: {matchedSource.MatchTitle}");
			logger.LogInformation("▶️ YouTube source resumed via gRPC trigger: {Title}", matchedSource.MatchTitle);
			return Task.FromResult(new RunResult { Ok = true, Message = $"Resumed: {matchedSource.MatchTitle}" });
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "gRPC YouTubeResume trigger failed");
			return Task.FromResult(new RunResult { Ok = false, Message = ex.Message });
		}
	}

	/// <summary>
	/// Mirrors the /resetquality Telegram command: clear pending quality items + their
	/// processed-RSS guard entries, then re-trigger an RSS feed check.
	/// </summary>
	public override async Task<RunResult> ResetQuality(Empty request, ServerCallContext context)
	{
		try
		{
			var beforeCount = db.PendingDownloads.Count(p => p.Status == PendingStatus.WaitingForQuality);

			var pendingItems = db.PendingDownloads
				.Find(p => p.Status == PendingStatus.WaitingForQuality)
				.ToList();

			foreach (var item in pendingItems)
			{
				item.Status = PendingStatus.Rejected;
				db.PendingDownloads.Update(item);
				logger.LogInformation("Reset pending quality item: {Title}", item.RssTitle);
			}

			var processedGuids = pendingItems
				.Select(p => p.RssTitle)
				.Distinct()
				.ToList();

			int clearedRssItems = 0;
			foreach (var title in processedGuids)
			{
				var deleted = db.ProcessedRssItems.DeleteMany(r => r.Title == title);
				clearedRssItems += deleted;
				logger.LogDebug("Cleared {Count} RSS items for: {Title}", deleted, title);
			}

			state.AddActivity($"Reset {beforeCount} quality notifications");
			logger.LogInformation("Reset {Count} pending quality downloads and {RssCount} RSS items", beforeCount, clearedRssItems);

			await rssMonitor.TriggerCheckAsync(context.CancellationToken);

			return new RunResult { Ok = true, Message = $"Cleared {beforeCount} pending notification(s); RSS scan complete." };
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "gRPC ResetQuality trigger failed");
			return new RunResult { Ok = false, Message = ex.Message };
		}
	}

	/// <summary>Mirrors the /speedmode (/turtle) Telegram command: toggle Transmission's alt-speed (turtle) mode.</summary>
	public override async Task<RunResult> ToggleSpeedMode(Empty request, ServerCallContext context)
	{
		try
		{
			var current = await transmission.GetAltSpeedEnabledAsync(context.CancellationToken);
			if (current == null)
				return new RunResult { Ok = false, Message = "Could not reach Transmission. Check your settings." };

			var newState = !current.Value;
			var success = await transmission.SetAltSpeedAsync(newState, context.CancellationToken);
			if (!success)
				return new RunResult { Ok = false, Message = "Failed to change Transmission speed mode." };

			state.AddActivity($"Transmission speed mode: {(newState ? "alt/turtle" : "normal")}");
			var label = newState ? "Turtle (alt speed) mode enabled" : "Normal speed mode enabled";
			return new RunResult { Ok = true, Message = label };
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "gRPC ToggleSpeedMode trigger failed");
			return new RunResult { Ok = false, Message = ex.Message };
		}
	}

	// Queries
	public override Task<MediaBox.Control.Grpc.Status> GetStatus(Empty request, ServerCallContext context) => throw NotYet();
	public override Task<DownloadList> GetDownloads(Empty request, ServerCallContext context) => throw NotYet();
	public override Task<MediaList> GetLibrary(LibraryQuery request, ServerCallContext context) => throw NotYet();
	public override Task<WatchlistItems> GetWatchlist(Empty request, ServerCallContext context) => throw NotYet();
	public override Task<YouTubeSources> GetYouTubeSources(Empty request, ServerCallContext context) => throw NotYet();
	public override Task<SettingsMap> GetSettings(Empty request, ServerCallContext context) => throw NotYet();

	// Mutations
	public override Task<RunResult> AddWatchlist(TitleArg request, ServerCallContext context) => throw NotYet();
	public override Task<RunResult> RemoveWatchlist(TitleArg request, ServerCallContext context) => throw NotYet();
	public override Task<RunResult> SearchAndAddMovie(TitleArg request, ServerCallContext context) => throw NotYet();
	public override Task<RunResult> UpdateSettings(SettingsMap request, ServerCallContext context) => throw NotYet();
}
