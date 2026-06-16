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
	/// The keys this writer understands -- exactly the field set <see cref="Read"/> exposes.
	/// Anything else in <paramref name="values"/> (caller mistakes, future-proofing) is ignored.
	/// </summary>
	private static readonly HashSet<string> KnownKeys = new(StringComparer.Ordinal)
	{
		"TvShowsPath", "MoviesPath", "DownloadsPath", "YouTubePath", "UnknownPath",
		"TransmissionRpcUrl", "TransmissionUsername", "JellyfinUrl", "RssFeedUrl",
		"RssFeedCheckMinutes", "TransmissionCheckMinutes", "QualityWaitHours",
		"YouTubeDownloadPaused", "NewsSources"
	};

	/// <summary>
	/// Persists the editable, NON-SENSITIVE settings back to appsettings.json (git-tracked),
	/// mirroring the appsettings.json half of Settings.razor's SaveSettings (NOT
	/// SaveSecretsFile -- appsettings.Secrets.json is never read or written here).
	///
	/// Unlike the old Settings.razor handler (which rewrote the entire "MediaBox" section from
	/// its in-memory form state, blanking secret fields and re-asserting hardcoded defaults for
	/// fields it didn't surface), this reads the existing JSON DOM and only overwrites the keys
	/// present in <paramref name="values"/> that are in <see cref="KnownKeys"/>. Every other key
	/// already in the file -- including MediaBox keys not in the edited set (DatabasePath,
	/// CrashDataPath, FallbackRssFeedUrls, YtDlpArchivePath, DownloadOrganizerMinutes,
	/// WatchlistCheckHours, MediaScanHours, UseTowerTelegram, TowerGrpcUrl, and the secret
	/// placeholders TelegramBotToken/AuthPassword/TransmissionPassword/JellyfinApiKey) and every
	/// non-MediaBox top-level section (Logging, AllowedHosts, ...) -- is copied through verbatim.
	///
	/// IOptionsMonitor&lt;MediaBoxSettings&gt; has reloadOnChange enabled by default, so the new
	/// values take effect without an app restart.
	///
	/// Never throws: any failure is logged and returns false so callers (the gRPC handler) can
	/// report it back as a RunResult instead of an unhandled exception.
	/// </summary>
	public bool Write(Dictionary<string, string> values)
	{
		try
		{
			var path = Path.Combine(env.ContentRootPath, "appsettings.json");
			var json = File.ReadAllText(path);
			using var doc = JsonDocument.Parse(json);

			using var ms = new MemoryStream();
			using var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true });

			writer.WriteStartObject();
			foreach (var prop in doc.RootElement.EnumerateObject())
			{
				if (prop.Name == "MediaBox")
				{
					writer.WritePropertyName("MediaBox");
					WriteMediaBoxSection(writer, prop.Value, values);
				}
				else
				{
					prop.WriteTo(writer);
				}
			}
			writer.WriteEndObject();
			writer.Flush();

			File.WriteAllBytes(path, ms.ToArray());
			logger.LogInformation("Persisted {Count} MediaBox setting(s) to appsettings.json.", values.Count(v => KnownKeys.Contains(v.Key)));
			return true;
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Failed to write MediaBox settings to appsettings.json.");
			return false;
		}
	}

	/// <summary>
	/// Writes the "MediaBox" object: every existing key is copied through verbatim, except keys
	/// present in <paramref name="values"/> (and known to this writer) which are replaced with
	/// the parsed value from <paramref name="values"/>. Keys in <paramref name="values"/> that
	/// don't already exist in the section (shouldn't normally happen, since KnownKeys mirrors
	/// Read()) are appended.
	/// </summary>
	private static void WriteMediaBoxSection(Utf8JsonWriter writer, JsonElement existingMediaBox, Dictionary<string, string> values)
	{
		var applied = new HashSet<string>(StringComparer.Ordinal);

		writer.WriteStartObject();
		foreach (var prop in existingMediaBox.EnumerateObject())
		{
			if (KnownKeys.Contains(prop.Name) && values.TryGetValue(prop.Name, out var newValue))
			{
				if (TryWriteKnownValue(writer, prop.Name, newValue))
				{
					applied.Add(prop.Name);
					continue;
				}
				// Parse failed (e.g. malformed NewsSources JSON) -- keep the existing value rather
				// than risk wiping data with a bad caller payload.
			}
			prop.WriteTo(writer);
		}

		// Append any known key that was supplied but didn't already exist in the file.
		foreach (var (key, value) in values)
		{
			if (KnownKeys.Contains(key) && !applied.Contains(key) && !existingMediaBox.TryGetProperty(key, out _))
			{
				TryWriteKnownValue(writer, key, value);
			}
		}

		writer.WriteEndObject();
	}

	/// <summary>
	/// Parses a flat string value to the correct JSON type for the given known key and writes
	/// "key: value". Returns false (writing nothing) if the value can't be parsed for keys with a
	/// strict expected shape (currently only NewsSources) so the caller can keep the prior value
	/// instead of overwriting it with a default/empty fallback.
	/// </summary>
	private static bool TryWriteKnownValue(Utf8JsonWriter writer, string key, string rawValue)
	{
		switch (key)
		{
			case "RssFeedCheckMinutes":
			case "TransmissionCheckMinutes":
			case "QualityWaitHours":
				writer.WriteNumber(key, int.TryParse(rawValue, out var n) ? n : 0);
				return true;

			case "YouTubeDownloadPaused":
				writer.WriteBoolean(key, bool.TryParse(rawValue, out var b) && b);
				return true;

			case "NewsSources":
				try
				{
					var sources = JsonSerializer.Deserialize<List<NewsSource>>(rawValue);
					if (sources == null) return false;
					writer.WritePropertyName(key);
					JsonSerializer.Serialize(writer, sources);
					return true;
				}
				catch (JsonException)
				{
					return false;
				}

			default:
				// All remaining known keys are plain strings (paths, URLs, TransmissionUsername).
				writer.WriteString(key, rawValue ?? "");
				return true;
		}
	}
}
