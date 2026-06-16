using Grpc.Core;
using MediaBox.Control.Grpc;

namespace MediaBox2026.Services;

/// <summary>
/// gRPC control server for the Tower -> MediaBox channel (port 5602).
/// Scaffold only: every RPC currently throws Unimplemented. Real implementations
/// land in Tasks 3-5 (triggers, queries, mutations), wired to the existing
/// background services and Telegram command handler logic.
/// </summary>
public class MediaBoxControlService : MediaBoxControl.MediaBoxControlBase
{
	private static RpcException NotYet() =>
		new(new Grpc.Core.Status(StatusCode.Unimplemented, "not yet"));

	// Triggers
	public override Task<RunResult> Scan(Empty request, ServerCallContext context) => throw NotYet();
	public override Task<RunResult> Organize(Empty request, ServerCallContext context) => throw NotYet();
	public override Task<RunResult> RssCheck(Empty request, ServerCallContext context) => throw NotYet();
	public override Task<RunResult> TransmissionPoll(Empty request, ServerCallContext context) => throw NotYet();
	public override Task<RunResult> YouTubeDownload(Empty request, ServerCallContext context) => throw NotYet();
	public override Task<RunResult> YouTubePause(TitleArg request, ServerCallContext context) => throw NotYet();
	public override Task<RunResult> YouTubeResume(TitleArg request, ServerCallContext context) => throw NotYet();
	public override Task<RunResult> ResetQuality(Empty request, ServerCallContext context) => throw NotYet();
	public override Task<RunResult> ToggleSpeedMode(Empty request, ServerCallContext context) => throw NotYet();

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
