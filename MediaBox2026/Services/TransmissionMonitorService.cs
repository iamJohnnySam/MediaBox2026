using Microsoft.Extensions.Options;
using MediaBox2026.Models;

namespace MediaBox2026.Services;

public class TransmissionMonitorService(
    TransmissionClient transmission,
    ITelegramNotifier telegram,
    MediaBoxState state,
    IOptionsMonitor<MediaBoxSettings> settings,
    ILogger<TransmissionMonitorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("Transmission monitor waiting for Telegram readiness...");
        await state.WaitForTelegramReadyAsync(ct);
        await Task.Delay(TimeSpan.FromSeconds(20), ct);
        logger.LogInformation("Transmission monitor started");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await MonitorAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Transmission monitor error");
            }

            await Task.Delay(TimeSpan.FromMinutes(settings.CurrentValue.TransmissionCheckMinutes), ct);
        }
    }

    private async Task MonitorAsync(CancellationToken ct)
    {
        var torrents = await transmission.GetTorrentsAsync(ct);
        state.ActiveDownloads = torrents.Count(t => !t.IsFinished);
        state.NotifyChange();

        var completed = torrents.Where(t => t.IsFinished).ToList();
        foreach (var torrent in completed)
        {
            logger.LogInformation("Removing completed torrent: {Name}", torrent.Name);
            await transmission.RemoveTorrentAsync(torrent.Id, deleteData: false, ct);
            state.AddActivity($"Torrent completed: {torrent.Name}");
        }

        if (completed.Count > 0)
        {
            state.ActiveDownloads = torrents.Count - completed.Count;
            state.NotifyChange();
        }
    }
}
