using MediaBox2026.Models;
using Microsoft.Extensions.Options;

namespace MediaBox2026.Services;

public class JellyfinClient(
    IHttpClientFactory httpFactory,
    IOptionsMonitor<MediaBoxSettings> settings,
    ILogger<JellyfinClient> logger)
{
    public async Task TriggerLibraryScanAsync(CancellationToken ct = default)
    {
        var config = settings.CurrentValue;
        if (string.IsNullOrWhiteSpace(config.JellyfinUrl) || string.IsNullOrWhiteSpace(config.JellyfinApiKey))
            return;

        try
        {
            using var http = httpFactory.CreateClient();
            var url = $"{config.JellyfinUrl.TrimEnd('/')}/Library/Refresh";
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("X-Emby-Token", config.JellyfinApiKey);

            var response = await http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
                logger.LogInformation("Jellyfin library scan triggered");
            else
                logger.LogWarning("Jellyfin library scan failed: {Status}", response.StatusCode);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to trigger Jellyfin library scan");
        }
    }
}
