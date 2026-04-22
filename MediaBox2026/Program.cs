using MediaBox2026.Components;
using MediaBox2026.Models;
using MediaBox2026.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("http://0.0.0.0:5000");

// Load secrets file (gitignored) — overrides appsettings.json for sensitive values
// Use AppContext.BaseDirectory so the file is found next to the binary regardless of CWD
var secretsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.Secrets.json");
builder.Configuration.AddJsonFile(secretsPath, optional: true, reloadOnChange: true);

// Configure Serilog for file logging with instance-based logs
// Each app instance gets its own log file with timestamp
// Logs are saved in: Logs/YYYY/MM/mediabox-YYYYMMDD-HHmmss-fff.log
var startupTime = DateTime.Now;
var logsPath = Path.Combine(AppContext.BaseDirectory, "Logs");
var yearMonthPath = Path.Combine(logsPath, startupTime.ToString("yyyy"), startupTime.ToString("MM"));
Directory.CreateDirectory(yearMonthPath);

var logFileName = $"mediabox-{startupTime:yyyyMMdd-HHmmss-fff}.log";
var logFilePath = Path.Combine(yearMonthPath, logFileName);

Log.Logger = new LoggerConfiguration()
	.MinimumLevel.Information()
	.MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
	.Enrich.FromLogContext()
	.WriteTo.Console()
	.WriteTo.File(
		path: logFilePath,
		outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}",
		fileSizeLimitBytes: 100_000_000, // 100MB per file
		rollOnFileSizeLimit: true,
		shared: false,
		flushToDiskInterval: TimeSpan.FromSeconds(1))
	.CreateLogger();

builder.Host.UseSerilog();

// In-memory log sink for Blazor log viewer
var logSink = new InMemoryLogSink();
builder.Services.AddSingleton(logSink);
builder.Logging.AddProvider(new InMemoryLoggerProvider(logSink));

// Configuration
builder.Services.Configure<MediaBoxSettings>(builder.Configuration.GetSection("MediaBox"));

// Auto-detect OS and remap paths for Windows development vs Linux production
builder.Services.PostConfigure<MediaBoxSettings>(settings =>
{
	if (OperatingSystem.IsWindows())
	{
		settings.TvShowsPath = RemapLinuxPath(settings.TvShowsPath);
		settings.MoviesPath = RemapLinuxPath(settings.MoviesPath);
		settings.DownloadsPath = RemapLinuxPath(settings.DownloadsPath);
		settings.YouTubePath = RemapLinuxPath(settings.YouTubePath);
		settings.UnknownPath = RemapLinuxPath(settings.UnknownPath);

		if (settings.TransmissionRpcUrl.Contains("localhost") || settings.TransmissionRpcUrl.Contains("127.0.0.1"))
			settings.TransmissionRpcUrl = "http://192.168.1.30:9091/transmission/rpc";

		if (settings.YtDlpArchivePath.StartsWith("/"))
			settings.YtDlpArchivePath = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ytdl-archive.txt");

		if (settings.CrashDataPath.StartsWith("/"))
			settings.CrashDataPath = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MediaBox", "crashes");
	}

	static string RemapLinuxPath(string path)
	{
		const string linuxPrefix = "/molecule/Media/";
		if (path.StartsWith(linuxPrefix))
			return @"M:\" + path[linuxPrefix.Length..].Replace('/', '\\');
		return path;
	}
});

// Core services
builder.Services.AddSingleton<MediaDatabase>();
builder.Services.AddSingleton<MediaBoxState>();
builder.Services.AddSingleton<TelegramAuthStore>();
builder.Services.AddSingleton<TransmissionClient>();
builder.Services.AddSingleton<JellyfinClient>();
builder.Services.AddSingleton<MediaCatalogService>();
builder.Services.AddHttpClient();

// Telegram bot (singleton + hosted service + ITelegramNotifier)
builder.Services.AddSingleton<TelegramBotService>();
builder.Services.AddSingleton<ITelegramNotifier>(sp => sp.GetRequiredService<TelegramBotService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<TelegramBotService>());

// RSS Feed Monitor (singleton + hosted service for manual triggering)
builder.Services.AddSingleton<RssFeedMonitorService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RssFeedMonitorService>());

// Other background services
builder.Services.AddHostedService<MediaScannerService>();
builder.Services.AddHostedService<NewsRssFeedService>();
builder.Services.AddHostedService<TransmissionMonitorService>();
builder.Services.AddHostedService<DownloadOrganizerService>();
builder.Services.AddHostedService<MovieWatchlistService>();
builder.Services.AddHostedService<YouTubeDownloadService>();

// Crash reporter (subscribes to error logs, sends Telegram + saves crash data)
builder.Services.AddSingleton<CrashReporter>();

// Prevent BackgroundService failures from crashing the entire application
builder.Services.Configure<HostOptions>(opts =>
	opts.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

// Blazor
builder.Services.AddSingleton<IComponentActivator, FallbackComponentActivator>();
builder.Services.AddRazorComponents()
	.AddInteractiveServerComponents();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
	app.UseExceptionHandler("/Error", createScopeForErrors: true);
	app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAntiforgery();

// Health check endpoint
app.MapGet("/health", (MediaBoxState state, MediaDatabase db) =>
{
	var health = new
	{
		status = "healthy",
		timestamp = DateTime.UtcNow,
		lastRssCheck = state.LastRssCheck,
		lastMediaScan = state.LastMediaScan,
		services = new
		{
			database = "connected",
			rssMonitor = state.LastRssCheck.HasValue ? "running" : "not started",
			mediaScanner = state.LastMediaScan.HasValue ? "running" : "not started"
		},
		counts = new
		{
			tvShows = state.TvShowCount,
			movies = state.MovieCount,
			watchlist = state.WatchlistCount,
			activeDownloads = state.ActiveDownloads
		}
	};
	return Results.Ok(health);
}).AllowAnonymous();

app.MapStaticAssets();
app.MapRazorComponents<App>()
	.AddInteractiveServerRenderMode();

// Initialize crash reporter eagerly (subscribes to log events)
var crashReporter = app.Services.GetRequiredService<CrashReporter>();

AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
	if (e.ExceptionObject is Exception ex)
		crashReporter.SaveUnhandledException(ex, "UnhandledException");
};

TaskScheduler.UnobservedTaskException += (_, e) =>
{
	crashReporter.SaveUnhandledException(e.Exception, "UnobservedTaskException");
	e.SetObserved();
};

try
{
	Log.Information("MediaBox2026 starting up...");
	Log.Information("Environment: {Environment}, OS: {OS}", app.Environment.EnvironmentName, Environment.OSVersion);
	Log.Information("Application base directory: {BaseDir}", AppContext.BaseDirectory);
	Log.Information("Log file: {LogFile}", logFilePath);

	// Validate critical settings on startup
	var settings = app.Services.GetRequiredService<IOptions<MediaBoxSettings>>();
	ValidateSettings(settings.Value);

	app.Run();
}
catch (Exception ex)
{
	Log.Fatal(ex, "MediaBox2026 failed to start");
	throw;
}
finally
{
	Log.Information("MediaBox2026 shutting down...");
	await Log.CloseAndFlushAsync();
}

static void ValidateSettings(MediaBoxSettings settings)
{
	var issues = new List<string>();

	// Validate Telegram settings
	if (string.IsNullOrWhiteSpace(settings.TelegramBotToken))
		issues.Add("TelegramBotToken is not configured");

	if (!settings.TelegramChatId.HasValue)
		Log.Warning("TelegramChatId is not configured - notifications will require manual authentication");

	// Validate paths
	if (!Directory.Exists(settings.TvShowsPath))
		issues.Add($"TvShowsPath does not exist: {settings.TvShowsPath}");

	if (!Directory.Exists(settings.MoviesPath))
		issues.Add($"MoviesPath does not exist: {settings.MoviesPath}");

	if (!Directory.Exists(settings.DownloadsPath))
		issues.Add($"DownloadsPath does not exist: {settings.DownloadsPath}");

	// Validate RSS feed URL
	if (string.IsNullOrWhiteSpace(settings.RssFeedUrl))
		issues.Add("RssFeedUrl is not configured");
	else if (!Uri.TryCreate(settings.RssFeedUrl, UriKind.Absolute, out var rssUri) || 
			 (rssUri.Scheme != "http" && rssUri.Scheme != "https"))
		issues.Add($"RssFeedUrl is not a valid HTTP(S) URL: {settings.RssFeedUrl}");

	// Validate Transmission URL
	if (string.IsNullOrWhiteSpace(settings.TransmissionRpcUrl))
		issues.Add("TransmissionRpcUrl is not configured");
	else if (!Uri.TryCreate(settings.TransmissionRpcUrl, UriKind.Absolute, out var transUri))
		issues.Add($"TransmissionRpcUrl is not a valid URL: {settings.TransmissionRpcUrl}");

	// Validate timing settings
	if (settings.QualityWaitHours < 1 || settings.QualityWaitHours > 72)
		issues.Add($"QualityWaitHours should be between 1-72, got: {settings.QualityWaitHours}");

	if (settings.RssFeedCheckMinutes < 5 || settings.RssFeedCheckMinutes > 1440)
		issues.Add($"RssFeedCheckMinutes should be between 5-1440, got: {settings.RssFeedCheckMinutes}");

	if (issues.Count > 0)
	{
		Log.Warning("Configuration validation issues found:");
		foreach (var issue in issues)
			Log.Warning("  - {Issue}", issue);
	}
	else
	{
		Log.Information("✅ All critical settings validated successfully");
	}

	Log.Information("Quality wait hours: {Hours}h, RSS check: every {Minutes}min", 
		settings.QualityWaitHours, settings.RssFeedCheckMinutes);
}
