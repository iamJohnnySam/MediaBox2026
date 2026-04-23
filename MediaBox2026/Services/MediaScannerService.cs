using Microsoft.Extensions.Options;
using MediaBox2026.Models;

namespace MediaBox2026.Services;

public class MediaScannerService(
    MediaCatalogService catalog,
    MediaBoxState state,
    IOptionsMonitor<MediaBoxSettings> settings,
    ILogger<MediaScannerService> logger) : BackgroundService
{
    private int _consecutiveFailures = 0;
    private const int MaxConsecutiveFailures = 5;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("📺 Media scanner waiting for Telegram readiness...");

        try
        {
            await state.WaitForTelegramReadyAsync(ct);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Media scanner cancelled during Telegram wait");
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(5), ct);

        try
        {
            logger.LogInformation("🚀 Running initial media scan...");
            var scanStart = DateTime.UtcNow;
            await catalog.ScanAllAsync(ct);
            var duration = DateTime.UtcNow - scanStart;
            logger.LogInformation("✅ Initial media scan completed in {Duration:F1}s", duration.TotalSeconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogInformation("Media scanner cancelled during initial scan");
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "❌ Initial media scan failed");
            _consecutiveFailures++;
        }

        logger.LogInformation("🔄 Periodic scan interval: {Hours} hours", settings.CurrentValue.MediaScanHours);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromHours(settings.CurrentValue.MediaScanHours), ct);

                logger.LogInformation("=== Periodic Media Scan Starting ===");
                if (_consecutiveFailures > 0)
                {
                    logger.LogWarning("⚠️ Consecutive failures: {Count}/{Max}", _consecutiveFailures, MaxConsecutiveFailures);
                }

                var scanStart = DateTime.UtcNow;
                await catalog.ScanAllAsync(ct);
                _consecutiveFailures = 0; // Reset on success

                var duration = DateTime.UtcNow - scanStart;
                logger.LogInformation("✅ Periodic media scan completed in {Duration:F1}s", duration.TotalSeconds);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                logger.LogInformation("🛑 Media scanner shutting down...");
                break;
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                logger.LogError(ex, "❌ Periodic media scan error (consecutive failures: {Count}/{Max})", _consecutiveFailures, MaxConsecutiveFailures);

                if (_consecutiveFailures >= MaxConsecutiveFailures)
                {
                    logger.LogCritical("🚨 Media scanner reached max consecutive failures.");
                    // Continue anyway - scans are important
                }
            }
        }
    }
}
