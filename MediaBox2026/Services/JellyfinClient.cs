using MediaBox2026.Models;
using Microsoft.Extensions.Options;

namespace MediaBox2026.Services;

public class JellyfinClient(
    IHttpClientFactory httpFactory,
    IOptionsMonitor<MediaBoxSettings> settings,
    ILogger<JellyfinClient> logger)
{
    private static readonly TimeSpan ScanCooldown = TimeSpan.FromMinutes(10);
    private DateTime? _lastScanTriggeredAt;
    private int _scanInProgress;

    public async Task TriggerLibraryScanAsync(CancellationToken ct = default)
    {
        if (_lastScanTriggeredAt.HasValue && DateTime.UtcNow - _lastScanTriggeredAt.Value < ScanCooldown)
        {
            var remaining = ScanCooldown - (DateTime.UtcNow - _lastScanTriggeredAt.Value);
            logger.LogInformation("Jellyfin library scan skipped – cooldown active ({Remaining:mm\\:ss} remaining)", remaining);
            return;
        }

        if (Interlocked.CompareExchange(ref _scanInProgress, 1, 0) != 0)
        {
            logger.LogInformation("Jellyfin library scan skipped – scan already in progress");
            return;
        }

        try
        {
            _lastScanTriggeredAt = DateTime.UtcNow;

            var config = settings.CurrentValue;
            if (string.IsNullOrWhiteSpace(config.JellyfinUrl) || string.IsNullOrWhiteSpace(config.JellyfinApiKey))
                return;

            using var http = httpFactory.CreateClient();
            var url = $"{config.JellyfinUrl.TrimEnd('/')}/Library/Refresh";

            const int maxAttempts = 3;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, url);
                    request.Headers.Add("X-Emby-Token", config.JellyfinApiKey);

                    var response = await http.SendAsync(request, ct);
                    if (response.IsSuccessStatusCode)
                    {
                        logger.LogInformation("Jellyfin library scan triggered");
                        return;
                    }

                    logger.LogWarning("Jellyfin library scan failed: {Status}", response.StatusCode);
                    return;
                }
                catch (HttpRequestException ex) when (attempt < maxAttempts)
                {
                    var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt) * 2));
                    logger.LogWarning(ex, "Jellyfin scan request failed on attempt {Attempt}/{MaxAttempts}. Retrying in {Delay}.", attempt, maxAttempts, delay);
                    await Task.Delay(delay, ct);
                }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < maxAttempts)
                {
                    var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt) * 2));
                    logger.LogWarning("Jellyfin scan request timed out on attempt {Attempt}/{MaxAttempts}. Retrying in {Delay}.", attempt, maxAttempts, delay);
                    await Task.Delay(delay, ct);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to trigger Jellyfin library scan");
        }
        finally
        {
            Interlocked.Exchange(ref _scanInProgress, 0);
        }
    }
}
