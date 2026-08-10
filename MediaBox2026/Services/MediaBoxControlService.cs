using System.Text.Json;
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
	MovieWatchlistService watchlist,
	EpisodeGuideService episodeGuide,
	MediaBoxState state,
	MediaDatabase db,
	MediaBoxSettingsIo settingsIo,
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

			var sources = settings.CurrentValue.NewsSources;
			var matchedSource = sources.FirstOrDefault(s => s.MatchTitle.Contains(arg, StringComparison.OrdinalIgnoreCase));
			if (matchedSource is null)
				return Task.FromResult(new RunResult { Ok = false, Message = $"No source matching \"{arg}\" found." });

			state.PauseSource(matchedSource.MatchTitle);
			matchedSource.Paused = true;
			settingsIo.Write(new Dictionary<string, string> { ["NewsSources"] = JsonSerializer.Serialize(sources) });
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

			var sources = settings.CurrentValue.NewsSources;
			var matchedSource = sources.FirstOrDefault(s => s.MatchTitle.Contains(arg, StringComparison.OrdinalIgnoreCase));
			if (matchedSource is null)
				return Task.FromResult(new RunResult { Ok = false, Message = $"No source matching \"{arg}\" found." });

			state.ResumeSource(matchedSource.MatchTitle);
			matchedSource.Paused = false;
			settingsIo.Write(new Dictionary<string, string> { ["NewsSources"] = JsonSerializer.Serialize(sources) });
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

	/// <summary>
	/// Stop/start one torrent for Tower's downloads page. The hash is what Tower holds, and
	/// Transmission accepts it in "ids" directly, so nothing has to resolve a session id that
	/// may have been renumbered since the page last rendered.
	/// </summary>
	public override Task<RunResult> PauseTorrent(TorrentRef request, ServerCallContext context) =>
		SetTorrentPausedAsync(request.Hash, pause: true, context.CancellationToken);

	public override Task<RunResult> ResumeTorrent(TorrentRef request, ServerCallContext context) =>
		SetTorrentPausedAsync(request.Hash, pause: false, context.CancellationToken);

	private async Task<RunResult> SetTorrentPausedAsync(string hash, bool pause, CancellationToken ct)
	{
		var verb = pause ? "pause" : "resume";
		try
		{
			if (string.IsNullOrWhiteSpace(hash))
				return new RunResult { Ok = false, Message = "No torrent given." };

			var ok = pause
				? await transmission.PauseTorrentAsync(hash, ct)
				: await transmission.ResumeTorrentAsync(hash, ct);

			if (!ok)
				return new RunResult { Ok = false, Message = $"Transmission refused to {verb} that torrent." };

			state.AddActivity($"Torrent {verb}d via Tower: {hash[..Math.Min(8, hash.Length)]}");
			return new RunResult { Ok = true, Message = pause ? "Paused." : "Resumed." };
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "gRPC {Verb}Torrent failed for {Hash}", verb, hash);
			return new RunResult { Ok = false, Message = ex.Message };
		}
	}

	/// <summary>Mirrors MovieWatchlistService's per-cycle work (WatchlistCheck -> RunOnceAsync).</summary>
	public override async Task<RunResult> WatchlistCheck(Empty request, ServerCallContext context)
	{
		try
		{
			await watchlist.RunOnceAsync(context.CancellationToken);
			return new RunResult { Ok = true, Message = "Watchlist check complete" };
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "gRPC WatchlistCheck trigger failed");
			return new RunResult { Ok = false, Message = ex.Message };
		}
	}

	// Queries

	/// <summary>Mirrors the /status Telegram command: TV/movie/download counts + speed mode.</summary>
	public override async Task<MediaBox.Control.Grpc.Status> GetStatus(Empty request, ServerCallContext context)
	{
		try
		{
			bool speedMode;
			try
			{
				speedMode = await transmission.GetAltSpeedEnabledAsync(context.CancellationToken) ?? false;
			}
			catch (Exception ex)
			{
				logger.LogWarning(ex, "gRPC GetStatus: could not read Transmission speed mode");
				speedMode = false;
			}

			var summary = $"TV Shows: {state.TvShowCount} | Movies: {state.MovieCount} | " +
				$"Active Downloads: {state.ActiveDownloads} | Watchlist: {state.WatchlistCount} | " +
				$"YouTube: {state.YouTubeCount}";

			return new MediaBox.Control.Grpc.Status
			{
				TvShows = state.TvShowCount,
				Movies = state.MovieCount,
				ActiveDownloads = state.ActiveDownloads,
				SpeedMode = speedMode,
				Summary = summary
			};
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "gRPC GetStatus query failed");
			return new MediaBox.Control.Grpc.Status();
		}
	}

	/// <summary>Mirrors the /downloads Telegram command (TransmissionClient.GetTorrentsAsync).</summary>
	public override async Task<DownloadList> GetDownloads(Empty request, ServerCallContext context)
	{
		var result = new DownloadList();
		try
		{
			var torrents = await transmission.GetTorrentsAsync(context.CancellationToken);
			foreach (var tor in torrents)
			{
				result.Items.Add(new DownloadItem
				{
					Name = tor.Name,
					Percent = tor.PercentDone * 100,
					Status = tor.StatusText,
					SizeBytes = tor.TotalSize,
					RateDown = tor.RateDownload,
					Hash = tor.HashString,
					Paused = tor.Status == 0
				});
			}
			return result;
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "gRPC GetDownloads query failed");
			return new DownloadList();
		}
	}

	/// <summary>Mirrors the library views: TV shows or movies (by query.type) from MediaDatabase.</summary>
	public override Task<MediaList> GetLibrary(LibraryQuery request, ServerCallContext context)
	{
		var result = new MediaList();
		try
		{
			var type = request.Type?.Trim().ToLowerInvariant() ?? "";
			if (type == "tv")
			{
				foreach (var show in db.TvShows.FindAll())
				{
					result.Items.Add(new MediaItem
					{
						Name = show.Name,
						Year = show.Year?.ToString() ?? "",
						Seasons = show.LatestSeason,
						Path = show.FolderPath
					});
				}
			}
			else if (type == "movies")
			{
				foreach (var movie in db.Movies.FindAll())
				{
					result.Items.Add(new MediaItem
					{
						Name = movie.Name,
						Year = movie.Year?.ToString() ?? "",
						Seasons = 0,
						Path = movie.FolderPath
					});
				}
			}
			else
			{
				logger.LogWarning("gRPC GetLibrary: unrecognized type \"{Type}\" (expected \"tv\" or \"movies\")", request.Type);
			}
			return Task.FromResult(result);
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "gRPC GetLibrary query failed");
			return Task.FromResult(new MediaList());
		}
	}

	/// <summary>
	/// Aired episodes of one show (TVmaze) with a have/missing flag per episode.
	/// Errors are returned in the message's `error` field rather than as an RPC fault — Tower renders
	/// them inline, and a flaky third-party API must not surface as "MediaBox unreachable".
	/// </summary>
	public override async Task<EpisodeGuide> GetShowEpisodes(ShowQuery request, ServerCallContext context)
	{
		try
		{
			var guide = await episodeGuide.GetGuideAsync(request.Path ?? "", request.Name ?? "", context.CancellationToken);
			var result = new EpisodeGuide
			{
				Show = guide.Show,
				ImdbId = guide.ImdbId,
				Premiered = guide.Premiered,
				Error = guide.Error
			};
			foreach (var e in guide.Episodes)
			{
				result.Items.Add(new EpisodeEntry
				{
					Season = e.Season,
					Episode = e.Episode,
					Title = e.Title,
					AirDate = e.AirDate,
					Aired = e.Aired,
					Have = e.Have,
					Dispatched = e.Dispatched
				});
			}
			return result;
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "gRPC GetShowEpisodes query failed");
			return new EpisodeGuide { Error = ex.Message };
		}
	}

	/// <summary>Download candidates for one missing episode (EZTV), best-seeded first.</summary>
	public override async Task<TorrentOptions> SearchEpisodeTorrents(EpisodeTorrentQuery request, ServerCallContext context)
	{
		try
		{
			var (choices, error) = await episodeGuide.SearchEpisodeAsync(
				request.ImdbId ?? "", request.Season, request.Episode, context.CancellationToken);

			var result = new TorrentOptions { Error = error };
			foreach (var c in choices)
			{
				result.Items.Add(new TorrentOption
				{
					Title = c.Title,
					Quality = c.Quality,
					Seeds = c.Seeds,
					Peers = c.Peers,
					SizeBytes = c.SizeBytes,
					Magnet = c.Magnet,
					SeasonPack = c.SeasonPack,
					MeetsStandard = c.MeetsStandard
				});
			}
			return result;
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "gRPC SearchEpisodeTorrents query failed");
			return new TorrentOptions { Error = ex.Message };
		}
	}

	/// <summary>Sends a chosen magnet to Transmission (same path as an RSS auto-download).</summary>
	public override async Task<RunResult> AddEpisodeTorrent(AddEpisodeTorrentArg request, ServerCallContext context)
	{
		try
		{
			var (ok, message) = await episodeGuide.AddAsync(
				request.Magnet ?? "", request.Show ?? "", request.Season, request.Episode, context.CancellationToken);
			if (ok) state.AddActivity($"Manual download: {request.Show} S{request.Season:00}E{request.Episode:00}");
			return new RunResult { Ok = ok, Message = message };
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "gRPC AddEpisodeTorrent failed");
			return new RunResult { Ok = false, Message = ex.Message };
		}
	}

	/// <summary>Adds/lifts a manual tombstone so the RSS monitor skips (or resumes) an episode.</summary>
	public override Task<RunResult> SetEpisodeIgnored(EpisodeIgnoreArg request, ServerCallContext context)
	{
		try
		{
			var (ok, message) = episodeGuide.SetIgnored(
				request.Show ?? "", request.Season, request.Episode, request.Ignored);
			return Task.FromResult(new RunResult { Ok = ok, Message = message });
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "gRPC SetEpisodeIgnored failed");
			return Task.FromResult(new RunResult { Ok = false, Message = ex.Message });
		}
	}

	/// <summary>Season-wide tombstone: ignores (or lifts) every episode of a season not on disk.</summary>
	public override async Task<RunResult> SetSeasonIgnored(SeasonIgnoreArg request, ServerCallContext context)
	{
		try
		{
			var (ok, message) = await episodeGuide.SetSeasonIgnoredAsync(
				request.Path ?? "", request.Name ?? "", request.Season, request.Ignored, context.CancellationToken);
			return new RunResult { Ok = ok, Message = message };
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "gRPC SetSeasonIgnored failed");
			return new RunResult { Ok = false, Message = ex.Message };
		}
	}

	/// <summary>Mirrors the /watchlist Telegram command (Pending + AwaitingConfirmation items).</summary>
	public override Task<WatchlistItems> GetWatchlist(Empty request, ServerCallContext context)
	{
		var result = new WatchlistItems();
		try
		{
			var items = db.Watchlist.FindAll()
				.Where(w => w.Status is WatchlistStatus.Pending or WatchlistStatus.AwaitingConfirmation)
				.ToList();

			foreach (var item in items)
			{
				result.Items.Add(new MediaBox.Control.Grpc.WatchlistItem
				{
					Name = item.Year.HasValue ? $"{item.Name} ({item.Year})" : item.Name,
					Status = item.Status.ToString(),
					Quality = item.Quality ?? ""
				});
			}
			return Task.FromResult(result);
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "gRPC GetWatchlist query failed");
			return Task.FromResult(new WatchlistItems());
		}
	}

	/// <summary>Mirrors the /youtube listing: configured news sources + their paused state (persistent or temporary).</summary>
	public override Task<YouTubeSources> GetYouTubeSources(Empty request, ServerCallContext context)
	{
		var result = new YouTubeSources();
		try
		{
			foreach (var src in settings.CurrentValue.NewsSources)
			{
				var paused = src.Paused || state.IsSourceTemporarilyPaused(src.MatchTitle);
				result.Items.Add(new YouTubeSource
				{
					Title = src.MatchTitle,
					Url = src.Url,
					Paused = paused
				});
			}
			return Task.FromResult(result);
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "gRPC GetYouTubeSources query failed");
			return Task.FromResult(new YouTubeSources());
		}
	}

	/// <summary>Returns the editable settings (MediaBoxSettingsIo.Read) as a SettingsMap.</summary>
	public override Task<SettingsMap> GetSettings(Empty request, ServerCallContext context)
	{
		var result = new SettingsMap();
		try
		{
			foreach (var (key, value) in settingsIo.Read())
				result.Values[key] = value;
			return Task.FromResult(result);
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "gRPC GetSettings query failed");
			return Task.FromResult(new SettingsMap());
		}
	}

	// Mutations

	/// <summary>Mirrors the /add Telegram command: quick-add a title to the watchlist by name (no search).</summary>
	public override Task<RunResult> AddWatchlist(TitleArg request, ServerCallContext context)
	{
		try
		{
			var arg = request.Title?.Trim() ?? "";
			if (string.IsNullOrWhiteSpace(arg))
				return Task.FromResult(new RunResult { Ok = false, Message = "Usage: title required." });

			var parsed = FileNameParser.Parse(arg);
			db.Watchlist.Insert(new MediaBox2026.Models.WatchlistItem
			{
				Name = parsed.CleanName.Length > 0 ? parsed.CleanName : arg,
				Year = parsed.Year,
				Status = WatchlistStatus.Pending,
				AddedDate = DateTime.UtcNow
			});
			state.WatchlistCount = db.Watchlist.Count(w => w.Status == WatchlistStatus.Pending);
			state.AddActivity($"Added to watchlist: {arg}");
			logger.LogInformation("gRPC AddWatchlist: added \"{Title}\"", arg);
			return Task.FromResult(new RunResult { Ok = true, Message = $"Added to watchlist: {arg}" });
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "gRPC AddWatchlist mutation failed");
			return Task.FromResult(new RunResult { Ok = false, Message = ex.Message });
		}
	}

	/// <summary>Mirrors the /remove Telegram command: cancel the first watchlist entry whose name contains the title.</summary>
	public override Task<RunResult> RemoveWatchlist(TitleArg request, ServerCallContext context)
	{
		try
		{
			var arg = request.Title?.Trim() ?? "";
			if (string.IsNullOrWhiteSpace(arg))
				return Task.FromResult(new RunResult { Ok = false, Message = "Usage: title required." });

			// GetWatchlist renders "Name (Year)", so the web UI's Remove button sends that back
			// while the stored Name has no year — a raw Contains never matched. Split the year
			// off and match on the name, which also keeps /remove's partial titles working.
			var (wantName, wantYear) = FileNameParser.ParseFolderName(arg);

			var toRemove = db.Watchlist.FindAll()
				.Where(w => w.Status is WatchlistStatus.Pending
					or WatchlistStatus.Found or WatchlistStatus.AwaitingConfirmation)
				.Where(w => !wantYear.HasValue || !w.Year.HasValue || w.Year == wantYear)
				.FirstOrDefault(w => w.Name.Contains(wantName, StringComparison.OrdinalIgnoreCase)
					|| FileNameParser.FuzzyMatch(w.Name, wantName) >= 0.5);
			if (toRemove == null)
				return Task.FromResult(new RunResult { Ok = false, Message = $"Not found in watchlist: {arg}" });

			toRemove.Status = WatchlistStatus.Cancelled;
			db.Watchlist.Update(toRemove);
			state.WatchlistCount = db.Watchlist.Count(w => w.Status == WatchlistStatus.Pending);
			logger.LogInformation("gRPC RemoveWatchlist: removed \"{Title}\"", toRemove.Name);
			return Task.FromResult(new RunResult { Ok = true, Message = $"Removed from watchlist: {toRemove.Name}" });
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "gRPC RemoveWatchlist mutation failed");
			return Task.FromResult(new RunResult { Ok = false, Message = ex.Message });
		}
	}

	/// <summary>
	/// Mirrors the /movie (/search) Telegram command's search step (YTS lookup), but bounded for
	/// gRPC: the Telegram flow pages through results with inline Prev/Next/Add buttons and waits
	/// for a callback; there is no equivalent interactive round-trip over a single RPC call. So
	/// here we search, pick the best candidate (first/highest-rated result from YTS, matching
	/// /movie's default sort_by=rating), add it to the watchlist immediately (same fields the
	/// "✅ Add to Watchlist" button writes: Name/Year/ImdbCode/PosterUrl/TrailerCode/Pending), and
	/// report what was added.
	/// </summary>
	public override async Task<RunResult> SearchAndAddMovie(TitleArg request, ServerCallContext context)
	{
		try
		{
			var query = request.Title?.Trim() ?? "";
			if (string.IsNullOrWhiteSpace(query))
				return new RunResult { Ok = false, Message = "Usage: title required." };

			var (results, error) = await SearchYtsAsync(query, context.CancellationToken);
			if (error != null)
				return new RunResult { Ok = false, Message = error };

			var pick = request.ImdbCode?.Trim() ?? "";
			// A caller that already showed the user the list names its choice; everyone else
			// (Telegram, the old Tower button) still gets the top-rated hit.
			var best = pick.Length > 0
				? results.FirstOrDefault(m => m.ImdbCode == pick)
				: results.FirstOrDefault();

			if (best == null)
				return new RunResult { Ok = false, Message = $"No movies found for \"{query}\". Try a different search term." };

			db.Watchlist.Insert(new MediaBox2026.Models.WatchlistItem
			{
				Name = best.Title,
				Year = best.Year,
				ImdbCode = best.ImdbCode,
				PosterUrl = best.PosterUrl,
				TrailerCode = best.TrailerCode,
				Status = WatchlistStatus.Pending,
				AddedDate = DateTime.UtcNow
			});
			state.WatchlistCount = db.Watchlist.Count(w => w.Status == WatchlistStatus.Pending);
			state.AddActivity($"Added to watchlist: {best.Title} ({best.Year})");
			state.NotifyChange();
			logger.LogInformation("gRPC SearchAndAddMovie: matched \"{Query}\" -> \"{Title}\" ({Year}), added to watchlist", query, best.Title, best.Year);

			return new RunResult { Ok = true, Message = $"Added \"{best.Title} ({best.Year})\" to watchlist." };
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "gRPC SearchAndAddMovie mutation failed");
			return new RunResult { Ok = false, Message = ex.Message };
		}
	}

	/// <summary>
	/// The search half of SearchAndAddMovie on its own, so a UI can show the candidates and let
	/// the user pick: "Angry Birds" ranks Angry Birds 2 above The Angry Birds Movie, and adding
	/// the top hit silently is exactly the wrong answer when the query is ambiguous.
	/// </summary>
	public override async Task<MovieOptions> SearchMovies(TitleArg request, ServerCallContext context)
	{
		var result = new MovieOptions();
		try
		{
			var query = request.Title?.Trim() ?? "";
			if (string.IsNullOrWhiteSpace(query))
			{
				result.Error = "Usage: title required.";
				return result;
			}

			var (movies, error) = await SearchYtsAsync(query, context.CancellationToken);
			if (error != null)
			{
				result.Error = error;
				return result;
			}

			foreach (var m in movies)
			{
				result.Items.Add(new MovieOption
				{
					Title = m.Title,
					Year = m.Year,
					ImdbCode = m.ImdbCode ?? "",
					PosterUrl = m.PosterUrl ?? "",
					Rating = m.Rating
				});
			}

			if (result.Items.Count == 0)
				result.Error = $"No movies found for \"{query}\". Try a different search term.";

			return result;
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "gRPC SearchMovies query failed");
			result.Error = ex.Message;
			return result;
		}
	}

	private record YtsMovie(string Title, int Year, string? ImdbCode, string? PosterUrl, string? TrailerCode, double Rating);

	/// <summary>YTS lookup, sorted the way the Telegram /movie flow's first page is (by rating).</summary>
	private static async Task<(List<YtsMovie> Movies, string? Error)> SearchYtsAsync(string query, CancellationToken ct)
	{
		using var http = new HttpClient();
		var url = $"https://yts.bz/api/v2/list_movies.json?query_term={Uri.EscapeDataString(query)}&limit=10&sort_by=rating";
		var response = await http.GetAsync(url, ct);
		if (!response.IsSuccessStatusCode)
			return ([], "Movie search is temporarily unavailable. Please try again later.");

		var jsonText = await response.Content.ReadAsStringAsync(ct);
		using var doc = JsonDocument.Parse(jsonText);
		var json = doc.RootElement;
		if (!json.TryGetProperty("data", out var data) ||
			!data.TryGetProperty("movies", out var movies) ||
			movies.ValueKind != JsonValueKind.Array || movies.GetArrayLength() == 0)
		{
			return ([], $"No movies found for \"{query}\". Try a different search term.");
		}

		var list = movies.EnumerateArray().Select(m => new YtsMovie(
			m.GetProperty("title").GetString() ?? query,
			m.GetProperty("year").GetInt32(),
			m.TryGetProperty("imdb_code", out var ic) ? ic.GetString() : null,
			m.TryGetProperty("medium_cover_image", out var p) ? p.GetString() : null,
			m.TryGetProperty("yt_trailer_code", out var tr) ? tr.GetString() : null,
			m.TryGetProperty("rating", out var r) ? r.GetDouble() : 0)).ToList();

		return (list, null);
	}

	/// <summary>Persists non-sensitive settings changes (MediaBoxSettingsIo.Write) -- mirrors the appsettings.json half of Settings.razor's SaveSettings.</summary>
	public override Task<RunResult> UpdateSettings(SettingsMap request, ServerCallContext context)
	{
		try
		{
			var values = new Dictionary<string, string>(request.Values);
			var ok = settingsIo.Write(values);
			return Task.FromResult(ok
				? new RunResult { Ok = true, Message = $"Settings updated ({values.Count} value(s))." }
				: new RunResult { Ok = false, Message = "Failed to write settings. Check server logs." });
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "gRPC UpdateSettings mutation failed");
			return Task.FromResult(new RunResult { Ok = false, Message = ex.Message });
		}
	}
}
