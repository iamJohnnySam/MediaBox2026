using Microsoft.Extensions.Options;
using MediaBox2026.Models;

namespace MediaBox2026.Services;

public class MediaScannerService(
    MediaCatalogService catalog,
    MediaBoxState state,
    IOptionsMonitor<MediaBoxSettings> settings,
    ILogger<MediaScannerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("Media scanner waiting for Telegram readiness...");
        await state.WaitForTelegramReadyAsync(ct);
        await Task.Delay(TimeSpan.FromSeconds(5), ct);

        try
        {
            logger.LogInformation("Running initial media scan...");
            await catalog.ScanAllAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Initial media scan failed");
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromHours(settings.CurrentValue.MediaScanHours), ct);
                await catalog.ScanAllAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Periodic media scan error");
            }
        }
    }
}
