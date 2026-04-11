using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MediaBox2026.Models;
using Microsoft.Extensions.Options;

namespace MediaBox2026.Services;

public class TransmissionClient(IHttpClientFactory httpFactory, IOptionsMonitor<MediaBoxSettings> settings, ILogger<TransmissionClient> logger)
{
    private string? _sessionId;

    public async Task<bool> AddTorrentAsync(string url, CancellationToken ct = default)
    {
        var request = new JsonObject
        {
            ["method"] = "torrent-add",
            ["arguments"] = new JsonObject
            {
                ["filename"] = url,
                ["download-dir"] = settings.CurrentValue.DownloadsPath
            }
        };

        var result = await SendRpcAsync(request, ct);
        if (result != null)
        {
            logger.LogInformation("Torrent added: {Url}", url);
            return true;
        }
        return false;
    }

    public async Task<List<TorrentInfo>> GetTorrentsAsync(CancellationToken ct = default)
    {
        var fields = new JsonArray();
        foreach (var field in new[] { "id", "name", "status", "percentDone", "totalSize", "downloadDir", "addedDate" })
            fields.Add(JsonValue.Create(field));

        var request = new JsonObject
        {
            ["method"] = "torrent-get",
            ["arguments"] = new JsonObject
            {
                ["fields"] = fields
            }
        };

        var result = await SendRpcAsync(request, ct);
        if (result == null) return [];

        var torrents = new List<TorrentInfo>();
        if (result.Value.TryGetProperty("arguments", out var args) &&
            args.TryGetProperty("torrents", out var arr))
        {
            foreach (var t in arr.EnumerateArray())
            {
                torrents.Add(new TorrentInfo
                {
                    Id = t.GetProperty("id").GetInt32(),
                    Name = t.GetProperty("name").GetString() ?? "",
                    Status = t.GetProperty("status").GetInt32(),
                    PercentDone = t.GetProperty("percentDone").GetDouble(),
                    TotalSize = t.GetProperty("totalSize").GetInt64(),
                    DownloadDir = t.TryGetProperty("downloadDir", out var dd) ? dd.GetString() : null,
                    DateAdded = t.TryGetProperty("addedDate", out var da) ? da.GetInt64() : 0
                });
            }
        }
        return torrents;
    }

    public async Task RemoveTorrentAsync(int id, bool deleteData = false, CancellationToken ct = default)
    {
        var request = new JsonObject
        {
            ["method"] = "torrent-remove",
            ["arguments"] = new JsonObject
            {
                ["ids"] = new JsonArray { JsonValue.Create(id) },
                ["delete-local-data"] = deleteData
            }
        };
        await SendRpcAsync(request, ct);
        logger.LogInformation("Torrent removed: {Id} (deleteData: {Delete})", id, deleteData);
    }

    public async Task<bool> PauseTorrentAsync(int id, CancellationToken ct = default)
    {
        var request = new JsonObject
        {
            ["method"] = "torrent-stop",
            ["arguments"] = new JsonObject
            {
                ["ids"] = new JsonArray { JsonValue.Create(id) }
            }
        };
        var result = await SendRpcAsync(request, ct);
        if (result != null)
        {
            logger.LogInformation("Torrent paused: {Id}", id);
            return true;
        }
        return false;
    }

    public async Task<bool> ResumeTorrentAsync(int id, CancellationToken ct = default)
    {
        var request = new JsonObject
        {
            ["method"] = "torrent-start",
            ["arguments"] = new JsonObject
            {
                ["ids"] = new JsonArray { JsonValue.Create(id) }
            }
        };
        var result = await SendRpcAsync(request, ct);
        if (result != null)
        {
            logger.LogInformation("Torrent resumed: {Id}", id);
            return true;
        }
        return false;
    }

    private async Task<JsonElement?> SendRpcAsync(JsonObject request, CancellationToken ct, bool retry = true)
    {
        var config = settings.CurrentValue;
        using var http = httpFactory.CreateClient();

        if (!string.IsNullOrEmpty(config.TransmissionUsername))
        {
            var authBytes = Encoding.UTF8.GetBytes($"{config.TransmissionUsername}:{config.TransmissionPassword}");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));
        }

        if (_sessionId != null)
            http.DefaultRequestHeaders.Add("X-Transmission-Session-Id", _sessionId);

        var json = request.ToJsonString();
        logger.LogInformation("Transmission RPC request: {Json}", json);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await http.PostAsync(config.TransmissionRpcUrl, content, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            if (response.Headers.TryGetValues("X-Transmission-Session-Id", out var values))
            {
                _sessionId = values.FirstOrDefault();
                if (retry)
                    return await SendRpcAsync(request, ct, false);
            }
            logger.LogWarning("Transmission returned 409 but no session ID header");
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Transmission RPC error: {Status}", response.StatusCode);
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (root.TryGetProperty("result", out var resultProp) && resultProp.GetString() == "success")
            return root;

        logger.LogWarning("Transmission RPC returned: {Result}. Response body: {Body}", root.GetProperty("result").GetString(), body);
        return null;
    }
}
