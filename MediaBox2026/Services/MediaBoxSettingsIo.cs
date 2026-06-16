using System.Text.Json;
using MediaBox2026.Models;
using Microsoft.Extensions.Options;

namespace MediaBox2026.Services;

/// <summary>
/// Reads/writes the NON-SENSITIVE, editable MediaBoxSettings fields -- the same field set
/// exposed by the old Settings.razor UI (Components/Pages/Settings.razor). Secrets
/// (TelegramBotToken, AuthPassword, TransmissionPassword, JellyfinApiKey) are out of scope:
/// they live in appsettings.Secrets.json and are never read or written here.
/// </summary>
public class MediaBoxSettingsIo(
	IOptionsMonitor<MediaBoxSettings> settings,
	IHostEnvironment env,
	ILogger<MediaBoxSettingsIo> logger)
{
	/// <summary>
	/// Returns the editable settings as flat string key/value pairs, mirroring exactly the
	/// fields Settings.razor binds to (paths, Transmission/Jellyfin URLs, RSS URL, intervals,
	/// the YouTube pause flag, and the news sources list serialized as JSON).
	/// </summary>
	public Dictionary<string, string> Read()
	{
		var s = settings.CurrentValue;
		var values = new Dictionary<string, string>
		{
			["TvShowsPath"] = s.TvShowsPath,
			["MoviesPath"] = s.MoviesPath,
			["DownloadsPath"] = s.DownloadsPath,
			["YouTubePath"] = s.YouTubePath,
			["UnknownPath"] = s.UnknownPath,
			["TransmissionRpcUrl"] = s.TransmissionRpcUrl,
			["TransmissionUsername"] = s.TransmissionUsername ?? "",
			["JellyfinUrl"] = s.JellyfinUrl,
			["RssFeedUrl"] = s.RssFeedUrl,
			["RssFeedCheckMinutes"] = s.RssFeedCheckMinutes.ToString(),
			["TransmissionCheckMinutes"] = s.TransmissionCheckMinutes.ToString(),
			["QualityWaitHours"] = s.QualityWaitHours.ToString(),
			["YouTubeDownloadPaused"] = s.YouTubeDownloadPaused.ToString(),
			["NewsSources"] = JsonSerializer.Serialize(s.NewsSources)
		};
		return values;
	}

	/// <summary>
	/// Persists the editable settings back to appsettings.json (non-sensitive, git-tracked).
	/// Implemented in Task 5 (mutation RPCs + settings writer) -- mirrors Settings.razor's
	/// SaveSettings, which rewrites appsettings.json's "MediaBox" section while leaving secrets
	/// untouched (and routed instead to appsettings.Secrets.json).
	/// </summary>
	public void Write(Dictionary<string, string> values)
	{
		logger.LogWarning("MediaBoxSettingsIo.Write is not yet implemented (Task 5) -- ignoring {Count} value(s).", values.Count);
	}
}
