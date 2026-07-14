using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Seedrcc;

namespace JellySeedr.Api;

/// <summary>
/// Mock Transmission RPC endpoint for Radarr/Sonarr integration.
/// Auth: HTTP Basic (password = TransmissionToken). CSRF: X-Transmission-Session-Id.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("jellyseedr/mocktransmission")]
public class MockTransmissionController : ControllerBase
{
    private static readonly string _sessionId = Guid.NewGuid().ToString("N");
    private static readonly ConcurrentDictionary<uint, MockTransmissionTorrent> _torrents = new();
    private static uint _torrentIdCounter = 0;
    private static readonly SeedrManager _seedrManager = SeedrManager.Instance;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<MockTransmissionController> _logger;

    public MockTransmissionController(IHttpClientFactory httpClientFactory, ILogger<MockTransmissionController> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        SeedrManager.Instance._logger ??= logger;
    }

    // -------------------------------------------------------------------------
    // RPC Dispatch
    // -------------------------------------------------------------------------

    [HttpPost("rpc")]
    [HttpGet("rpc")]
    public async Task<IActionResult> Rpc()
    {
            if (!ValidateBasicAuth(Request, _logger, out var authError))
        {
            Response.Headers["WWW-Authenticate"] = "Basic realm=\"JellySeedr\"";
            return StatusCode(401, authError);
        }

        var clientSessionId = Request.Headers["X-Transmission-Session-Id"].FirstOrDefault();
        if (clientSessionId != _sessionId)
        {
            Response.Headers["X-Transmission-Session-Id"] = _sessionId;
            return StatusCode(409, "CSRF protection: retry with the X-Transmission-Session-Id header.");
        }

        JsonNode? body;
        try
        {
            using var reader = new StreamReader(Request.Body, Encoding.UTF8);
            var raw = await reader.ReadToEndAsync();
            body = JsonNode.Parse(raw);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MockTransmission: Failed to parse request body.");
            return BadRequest(ErrorResponse("parse-error", "Invalid JSON body."));
        }

        var method = body?["method"]?.GetValue<string>() ?? string.Empty;
        var arguments = body?["arguments"] as JsonObject ?? new JsonObject();
        var tag = body?["tag"]?.GetValue<int?>();

        var result = method switch
        {
            "torrent-add" => await HandleTorrentAdd(arguments, tag),
            "torrent-get" => HandleTorrentGet(arguments, tag),
            "torrent-remove" => await HandleTorrentRemove(arguments, tag),
            "session-get" => HandleSessionGet(tag),
            "session-stats" => HandleSessionStats(tag),
            _ => UnsupportedMethod(method, tag)
        };

        return result;
    }

    // -------------------------------------------------------------------------
    // Method handlers
    // -------------------------------------------------------------------------

    private async Task<IActionResult> HandleTorrentAdd(JsonObject args, int? tag)
    {
        var config = Plugin.Instance!.Configuration;
        var downloadPath = config.TransmissionDownloadPath;

        if (string.IsNullOrWhiteSpace(downloadPath))
            return Ok(ErrorResponse("torrent-add", "Download path is not configured. Set it in the JellySeedr plugin preferences.", tag));

        var client = await _seedrManager.EnsureClientAsync(_httpClientFactory);
        if (client == null)
            return Ok(ErrorResponse("torrent-add", "Not logged in to Seedr. Log in via the JellySeedr preferences page.", tag));

        if (!TryBuildTorrentParam(args, config, downloadPath, out var param, out var displayName, out var parseError))
            return Ok(ErrorResponse("torrent-add", parseError, tag));

        var (_, queueId, _) = _seedrManager.EnqueueTorrent(client, param, displayName);

        var torrentId = System.Threading.Interlocked.Increment(ref _torrentIdCounter);
        var hashString = (param.InputType == SeedrInputType.TorrentFile
            ? GetInfoHash(param.TorrentBytes)
            : ExtractMagnetHash(param.Source))
            ?? queueId.ToString("x8");

        _torrents[torrentId] = new MockTransmissionTorrent
        {
            Id = torrentId,
            QueueId = queueId,
            Name = displayName,
            DownloadPath = downloadPath,
            HashString = hashString
        };

        return Ok(SuccessResponse("torrent-add", new JsonObject
        {
            ["torrent-added"] = new JsonObject
            {
                ["id"] = torrentId,
                ["name"] = displayName,
                ["hashString"] = hashString
            }
        }, tag));
    }

    private IActionResult HandleTorrentGet(JsonObject args, int? tag)
    {
        var queueMap = _seedrManager.GetQueue().ToDictionary(q => q.QueueId);
        var requestedIds = GetRequestedTorrentIds(args);

        var list = new JsonArray();
        foreach (var (torrentId, entry) in _torrents)
        {
            if (requestedIds != null && !requestedIds.Contains(torrentId)) continue;
            queueMap.TryGetValue(entry.QueueId, out var queueItem);
            list.Add(BuildTorrentObject(torrentId, entry, queueItem));
        }

        return Ok(SuccessResponse("torrent-get", new JsonObject { ["torrents"] = list }, tag));
    }

    private async Task<IActionResult> HandleTorrentRemove(JsonObject args, int? tag)
    {
        var requestedIds = GetRequestedTorrentIds(args);

        bool deleteLocalData = false;
        if (args["delete-local-data"] is JsonValue dldVal)
        {
            if (dldVal.TryGetValue<bool>(out var bVal)) deleteLocalData = bVal;
            else if (dldVal.TryGetValue<int>(out var iVal)) deleteLocalData = iVal != 0;
        }

        if (requestedIds != null)
        {
            foreach (var id in requestedIds)
            {
                if (!_torrents.TryRemove(id, out var entry)) continue;
                await _seedrManager.CancelQueueItemAsync(entry.QueueId);

                if (deleteLocalData && !string.IsNullOrWhiteSpace(entry.DownloadPath) && !string.IsNullOrWhiteSpace(entry.Name))
                    DeleteLocalData(Path.Combine(entry.DownloadPath, entry.Name), entry.Name);
            }
        }

        return Ok(SuccessResponse("torrent-remove", new JsonObject(), tag));
    }

    private IActionResult HandleSessionGet(int? tag)
    {
        var downloadDir = Plugin.Instance!.Configuration.TransmissionDownloadPath ?? string.Empty;
        return Ok(SuccessResponse("session-get", new JsonObject
        {
            ["version"] = "2.94 (JellySeedr)",
            ["download-dir"] = downloadDir,
            ["rpc-version"] = 17,
            ["rpc-version-minimum"] = 14,
            ["encryption"] = "preferred"
        }, tag));
    }

    private IActionResult HandleSessionStats(int? tag)
    {
        var queueSnapshot = _seedrManager.GetQueue();
        var activeCount = _torrents.Count(t =>
            queueSnapshot.FirstOrDefault(x => x.QueueId == t.Value.QueueId)?.Status == QueuedTorrentStatus.Active);

        return Ok(SuccessResponse("session-stats", new JsonObject
        {
            ["activeTorrentCount"] = activeCount,
            ["pausedTorrentCount"] = _torrents.Count - activeCount,
            ["torrentCount"] = _torrents.Count
        }, tag));
    }

    private IActionResult UnsupportedMethod(string method, int? tag)
    {
        _logger.LogWarning("MockTransmission: Unsupported method '{Method}'.", method);
        return Ok(ErrorResponse(method, $"Method '{method}' not supported.", tag));
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>Builds a SeedrTorrentAddParam from the RPC torrent-add arguments.</summary>
    private static bool TryBuildTorrentParam(
        JsonObject args, JellySeedr.Configuration.PluginConfiguration config, string downloadPath,
        out SeedrTorrentAddParam param, out string displayName, out string error)
    {
        param = null!; displayName = string.Empty; error = string.Empty;

        var metainfoBs64 = args["metainfo"]?.GetValue<string>();
        var filename = args["filename"]?.GetValue<string>();

        if (!string.IsNullOrEmpty(metainfoBs64))
        {
            byte[] torrentBytes;
            try { torrentBytes = Convert.FromBase64String(metainfoBs64); }
            catch { error = "Invalid base64 torrent data."; return false; }

            displayName = args["name"]?.GetValue<string>() ?? ExtractTorrentName(torrentBytes) ?? "torrent";
            param = MakeParam(SeedrInputType.TorrentFile, downloadPath, config, torrentBytes: torrentBytes);
        }
        else if (!string.IsNullOrEmpty(filename))
        {
            if (filename.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            {
                displayName = ExtractMagnetName(filename) ?? filename;
                param = MakeParam(SeedrInputType.MagnetLink, downloadPath, config, source: filename);
            }
            else
            {
                displayName = ExtractUrlFileName(filename) ?? "Unknown Torrent";
                param = MakeParam(SeedrInputType.TorrentUrl, downloadPath, config, source: filename);
            }
        }
        else
        {
            error = "No torrent source provided (filename or metainfo required).";
            return false;
        }

        return true;
    }

    private static SeedrTorrentAddParam MakeParam(
        SeedrInputType inputType, string downloadPath, JellySeedr.Configuration.PluginConfiguration config,
        string? source = null, byte[]? torrentBytes = null) => new()
        {
            InputType = inputType,
            Source = source ?? string.Empty,
            TorrentBytes = torrentBytes!,
            DestinationPath = downloadPath,
            DeleteAfterDownload = config.DeleteAfterDownload,
            DownloadExtensions = config.DownloadFileTypes,
            ClashResolution = FetchNameClashResolution.Rename,
            DownloadAll = true,
            UseSubfolderStructure = true
        };

    /// <summary>Resolves "ids" from a torrent-get/remove request to a set of torrent IDs (by number or hash).</summary>
    private HashSet<uint>? GetRequestedTorrentIds(JsonObject args)
    {
        var idsNode = args["ids"];
        if (idsNode == null) return null;

        // "recently-active" → return all
        if (idsNode is JsonValue strCheck && strCheck.TryGetValue<string>(out var sv) && sv == "recently-active")
            return null;

        var result = new HashSet<uint>();

        // Normalize: single value or array
        IEnumerable<JsonNode?> items = idsNode is JsonArray arr ? arr : new[] { idsNode };

        foreach (var item in items)
        {
            if (item is not JsonValue jval) continue;
            if (jval.TryGetValue<uint>(out var id))
                result.Add(id);
            else if (jval.TryGetValue<string>(out var hash))
            {
                var match = _torrents.Values.FirstOrDefault(t => t.HashString.Equals(hash, StringComparison.OrdinalIgnoreCase));
                if (match != null) result.Add(match.Id);
            }
        }
        return result;
    }

    private JsonObject BuildTorrentObject(uint id, MockTransmissionTorrent entry, QueuedTorrent? queue)
    {
        var status = MapStatus(queue);
        double percentDone = 0;
        long totalSize = 0;
        long leftUntilDone = 0;

        if (queue != null)
        {
            if (queue.Status == QueuedTorrentStatus.Completed)
            {
                percentDone = 1.0;
                totalSize = queue.FetchTotalBytes;
            }
            else if (queue.Stage == QueuedTorrentStage.Torrenting)
            {
                percentDone = queue.TorrentProgress / 100.0;
                totalSize = queue.TorrentTotalBytes;
                leftUntilDone = (long)(totalSize * (1 - percentDone));
            }
            else if (queue.Stage == QueuedTorrentStage.Fetching)
            {
                var fb = queue.FetchTotalBytes;
                var fc = queue.FetchCopiedBytes;
                percentDone = fb > 0 ? (double)fc / fb : 0;
                totalSize = fb;
                leftUntilDone = fb - fc;
            }
        }

        var secondsElapsed = queue != null ? (long)(DateTime.UtcNow - queue.QueuedAt).TotalSeconds : 0;
        var isFinished = status == 6 || percentDone >= 1.0;

        return new JsonObject
        {
            ["id"] = id,
            ["name"] = entry.Name,
            ["hashString"] = entry.HashString,
            ["downloadDir"] = entry.DownloadPath,
            ["status"] = status,
            ["percentDone"] = percentDone,
            ["totalSize"] = totalSize,
            ["leftUntilDone"] = leftUntilDone,
            ["isFinished"] = isFinished,
            ["eta"] = -1,
            ["rateDownload"] = 0,
            ["rateUpload"] = 0,
            ["uploadRatio"] = 0,   // meets seedRatioLimit of 0 → Radarr triggers cleanup
            ["error"] = 0,
            ["errorString"] = queue?.ErrorMessage ?? string.Empty,
            ["secondsDownloading"] = secondsElapsed,
            ["secondsSeeding"] = isFinished ? secondsElapsed : 0,
            ["uploadedEver"] = 0,
            ["downloadedEver"] = totalSize - leftUntilDone,
            ["seedRatioLimit"] = 0,
            ["seedRatioMode"] = 1,   // 1 = stop at ratio limit
            ["seedIdleLimit"] = 0,
            ["seedIdleMode"] = 1,   // 1 = stop when idle
            ["fileCount"] = 1,
            ["file-count"] = 1,
            ["labels"] = new JsonArray()
        };
    }

    private static int MapStatus(QueuedTorrent? q) => q?.Status switch
    {
        QueuedTorrentStatus.Queued => 3,
        QueuedTorrentStatus.Active => 4,
        QueuedTorrentStatus.Completed => 6,
        _ => 0
    };

    private void DeleteLocalData(string fullPath, string name)
    {
        try
        {
            if (Directory.Exists(fullPath))
                Directory.Delete(fullPath, true);
            else if (System.IO.File.Exists(fullPath))
                System.IO.File.Delete(fullPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MockTransmission [torrent-remove]: Failed to delete local data for '{Name}'.", name);
        }
    }

    private static bool ValidateBasicAuth(HttpRequest request, ILogger logger, out string error)
    {
        error = string.Empty;
        var authHeader = request.Headers["Authorization"].FirstOrDefault();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            error = "Missing or invalid Authorization header. Use HTTP Basic Auth with the Transmission token as password.";
            return false;
        }

        string decoded;
        try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authHeader["Basic ".Length..].Trim())); }
        catch { error = "Malformed Basic Auth credentials."; return false; }

        var colonIdx = decoded.IndexOf(':');
        var password = colonIdx >= 0 ? decoded[(colonIdx + 1)..] : decoded;

        if (!CryptographicEqual(password, Plugin.Instance!.GetOrCreateTransmissionToken()))
        {
            error = "Invalid token.";
            return false;
        }

        return true;
    }

    private static bool CryptographicEqual(string a, string b)
    {
        if (a.Length != b.Length) return false;
        int result = 0;
        for (int i = 0; i < a.Length; i++) result |= a[i] ^ b[i];
        return result == 0;
    }

    // ---- Bencode / hash helpers ----

    private static string? ExtractTorrentName(byte[] data)
    {
        return ReadBencodeString(data, "4:name"u8.ToArray());
    }

    private static string? ReadBencodeString(byte[] data, byte[] marker)
    {
        for (int i = 0; i <= data.Length - marker.Length; i++)
        {
            if (!MatchesAt(data, i, marker)) continue;

            int pos = i + marker.Length;
            int colon = pos;
            while (colon < data.Length && data[colon] != (byte)':') colon++;
            if (colon >= data.Length) continue;

            var lenStr = Encoding.ASCII.GetString(data, pos, colon - pos);
            if (!int.TryParse(lenStr, out int len) || len <= 0 || len > 4096) continue;

            int nameStart = colon + 1;
            if (nameStart + len > data.Length) continue;
            return Encoding.UTF8.GetString(data, nameStart, len);
        }
        return null;
    }

    private static string? GetInfoHash(byte[]? data)
    {
        if (data == null) return null;
        var marker = "4:info"u8.ToArray();
        for (int i = 0; i <= data.Length - marker.Length; i++)
        {
            if (!MatchesAt(data, i, marker)) continue;
            int infoStart = i + marker.Length;
            if (infoStart >= data.Length || data[infoStart] != (byte)'d') continue;

            int infoEnd = FindDictionaryEnd(data, infoStart);
            if (infoEnd == -1) continue;

            using var sha1 = System.Security.Cryptography.SHA1.Create();
            return BitConverter.ToString(sha1.ComputeHash(data, infoStart, infoEnd - infoStart + 1))
                .Replace("-", "").ToLowerInvariant();
        }
        return null;
    }

    private static bool MatchesAt(byte[] data, int pos, byte[] marker)
    {
        for (int j = 0; j < marker.Length; j++)
            if (data[pos + j] != marker[j]) return false;
        return true;
    }

    private static int FindDictionaryEnd(byte[] data, int start)
    {
        int i = start + 1, depth = 1;
        while (i < data.Length && depth > 0)
        {
            byte c = data[i];
            if (c == (byte)'d' || c == (byte)'l') { depth++; i++; }
            else if (c == (byte)'e') { depth--; if (depth == 0) return i; i++; }
            else if (c == (byte)'i') { i++; while (i < data.Length && data[i] != (byte)'e') i++; if (i < data.Length) i++; }
            else if (c >= (byte)'0' && c <= (byte)'9')
            {
                int colon = i;
                while (colon < data.Length && data[colon] != (byte)':') colon++;
                if (colon >= data.Length) return -1;
                if (!int.TryParse(Encoding.ASCII.GetString(data, i, colon - i), out int len)) return -1;
                i = colon + 1 + len;
            }
            else return -1;
        }
        return depth == 0 ? i : -1;
    }

    private static string? ExtractMagnetHash(string url)
    {
        if (!url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(url, @"urn:btih:([a-zA-Z0-9]+)");
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
    }

    private static string? ExtractMagnetName(string magnet)
    {
        var m = System.Text.RegularExpressions.Regex.Match(magnet, @"dn=([^&]+)");
        return m.Success ? System.Net.WebUtility.UrlDecode(m.Groups[1].Value) : null;
    }

    private static string? ExtractUrlFileName(string url)
    {
        try { return Path.GetFileNameWithoutExtension(Uri.UnescapeDataString(new Uri(url).Segments.Last())); }
        catch { return url; }
    }

    private static JsonObject SuccessResponse(string method, JsonObject arguments, int? tag = null)
    {
        var obj = new JsonObject { ["result"] = "success", ["arguments"] = arguments };
        if (tag.HasValue) obj["tag"] = tag.Value;
        return obj;
    }

    private static JsonObject ErrorResponse(string method, string message, int? tag = null)
    {
        var obj = new JsonObject { ["result"] = message, ["arguments"] = new JsonObject() };
        if (tag.HasValue) obj["tag"] = tag.Value;
        return obj;
    }
}

public sealed class MockTransmissionTorrent
{
    public uint Id { get; set; }
    public uint QueueId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DownloadPath { get; set; } = string.Empty;
    public string HashString { get; set; } = string.Empty;
}
