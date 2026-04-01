using Microsoft.Extensions.Options;
using MediaBox2026.Models;

namespace MediaBox2026.Services;

public class MediaScannerService(
    MediaCatalogService catalog,
    IOptionsMonitor<MediaBoxSettings> settings,
    ILogger<MediaScannerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), ct);

        logger.LogInformation("Running initial media scan...");
        await catalog.ScanAllAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromHours(settings.CurrentValue.MediaScanHours), ct);
            try
            {
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
