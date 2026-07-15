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
///
/// Supported methods (all methods Radarr's TransmissionProxy calls):
///   torrent-add    — filename/metainfo, paused, download-dir, labels
///   torrent-get    — ids (int/hash/array/"recently-active"), fields (honoured for filtering)
///   torrent-set    — ids, seedRatioLimit, seedRatioMode, seedIdleLimit, seedIdleMode, labels
///   torrent-remove — ids, delete-local-data
///   queue-move-top — ids  (no-op; Seedr has no queue-priority concept)
///   session-get    — returns version, download-dir, rpc-version, rpc-version-minimum, encryption
///   session-stats  — returns activeTorrentCount, pausedTorrentCount, torrentCount
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
        var tag = body?["tag"] is JsonNode tagNode ? (int?)tagNode.GetValue<int>() : null;

        var result = method switch
        {
            "torrent-add"    => await HandleTorrentAdd(arguments, tag),
            "torrent-get"    => HandleTorrentGet(arguments, tag),
            "torrent-set"    => HandleTorrentSet(arguments, tag),
            "torrent-remove" => await HandleTorrentRemove(arguments, tag),
            "queue-move-top" => HandleQueueMoveTop(arguments, tag),
            "session-get"    => HandleSessionGet(tag),
            "session-stats"  => HandleSessionStats(tag),
            _                => UnsupportedMethod(method, tag)
        };

        return result;
    }

    // -------------------------------------------------------------------------
    // Method handlers
    // -------------------------------------------------------------------------

    private async Task<IActionResult> HandleTorrentAdd(JsonObject args, int? tag)
    {
        var config = Plugin.Instance!.Configuration;

        // Resolve download directory: prefer the per-request "download-dir" argument,
        // fall back to the plugin-configured path.
        var downloadPath = args["download-dir"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(downloadPath))
            downloadPath = config.TransmissionDownloadPath;

        if (string.IsNullOrWhiteSpace(downloadPath))
            return Ok(ErrorResponse("torrent-add", "Download path is not configured. Set it in the JellySeedr plugin preferences.", tag));

        var client = await _seedrManager.EnsureClientAsync(_httpClientFactory);
        if (client == null)
            return Ok(ErrorResponse("torrent-add", "Not logged in to Seedr. Log in via the JellySeedr preferences page.", tag));

        if (!TryBuildTorrentParam(args, config, downloadPath, out var param, out var displayName, out var parseError))
            return Ok(ErrorResponse("torrent-add", parseError, tag));

        // Radarr sends "paused": true/false — we note it but Seedr always starts immediately.
        // No-op here; kept for spec completeness.
        _ = args["paused"] is JsonValue pausedVal && pausedVal.TryGetValue<bool>(out var paused) && paused;

        // Collect labels sent with the add request (Transmission 4.0+ feature).
        var labels = ParseLabels(args);

        string addedBy = "";
        if (Request?.Headers != null && Request.Headers.TryGetValue("User-Agent", out var uaValues))
        {
            var ua = uaValues.ToString();
            if (ua.Contains("Radarr", StringComparison.OrdinalIgnoreCase)) addedBy = "radarr";
            else if (ua.Contains("Sonarr", StringComparison.OrdinalIgnoreCase)) addedBy = "sonarr";
        }

        var hashString = param.InputType == SeedrInputType.TorrentFile
            ? GetInfoHash(param.TorrentBytes)
            : ExtractMagnetHash(param.Source);

        var (_, queueId, _) = _seedrManager.EnqueueTorrent(client, param, displayName, addedBy, hashString ?? "");

        var torrentId = System.Threading.Interlocked.Increment(ref _torrentIdCounter);
        if (hashString == null) hashString = queueId.ToString("x8");

        _torrents[torrentId] = new MockTransmissionTorrent
        {
            Id           = torrentId,
            QueueId      = queueId,
            Name         = displayName,
            DownloadPath = downloadPath,
            HashString   = hashString,
            Labels       = labels
        };

        return Ok(SuccessResponse("torrent-add", new JsonObject
        {
            ["torrent-added"] = new JsonObject
            {
                ["id"]         = torrentId,
                ["name"]       = displayName,
                ["hashString"] = hashString
            }
        }, tag));
    }

    private IActionResult HandleTorrentGet(JsonObject args, int? tag)
    {
        var queueMap = _seedrManager.GetQueue().ToDictionary(q => q.QueueId);
        var requestedIds = GetRequestedTorrentIds(args);

        // "fields" is sent by Radarr but the spec says unknown fields are ignored by the server;
        // we return all fields we support regardless, which is safe per spec.

        var list = new JsonArray();
        foreach (var (torrentId, entry) in _torrents)
        {
            if (requestedIds != null && !requestedIds.Contains(torrentId)) continue;
            queueMap.TryGetValue(entry.QueueId, out var queueItem);
            list.Add(BuildTorrentObject(torrentId, entry, queueItem));
        }

        return Ok(SuccessResponse("torrent-get", new JsonObject { ["torrents"] = list }, tag));
    }

    /// <summary>
    /// Handles torrent-set. Radarr uses this for two purposes:
    ///   1. SetTorrentSeedingConfiguration — sends seedRatioLimit/Mode, seedIdleLimit/Mode.
    ///      Seedr manages all seeding server-side, so these are intentionally ignored.
    ///   2. SetTorrentLabels / MarkItemAsImported — sends labels to update the post-import category.
    /// Both pass "ids" as a single-element list containing the torrent hash string.
    /// </summary>
    private IActionResult HandleTorrentSet(JsonObject args, int? tag)
    {
        var requestedIds = GetRequestedTorrentIds(args);
        if (requestedIds == null || requestedIds.Count == 0)
            return Ok(SuccessResponse("torrent-set", new JsonObject(), tag));

        foreach (var id in requestedIds)
        {
            if (!_torrents.TryGetValue(id, out var entry)) continue;

            // Labels (post-import category update via MarkItemAsImported).
            // Seed* parameters are ignored — Seedr handles seeding server-side.
            if (args["labels"] is JsonArray labelsArray)
                entry.Labels = labelsArray
                    .Select(n => n?.GetValue<string>())
                    .Where(s => s != null)
                    .Select(s => s!)
                    .ToList();
        }

        return Ok(SuccessResponse("torrent-set", new JsonObject(), tag));
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
                _seedrManager.UntrackAndRemoveIfHidden(entry.QueueId);

                if (deleteLocalData && !string.IsNullOrWhiteSpace(entry.DownloadPath) && !string.IsNullOrWhiteSpace(entry.Name))
                    DeleteLocalData(Path.Combine(entry.DownloadPath, entry.Name), entry.Name);
            }
        }

        return Ok(SuccessResponse("torrent-remove", new JsonObject(), tag));
    }

    /// <summary>
    /// Handles queue-move-top. Radarr calls this when RecentMoviePriority or OlderMoviePriority
    /// is set to "First". Moves the matching queued item to position 0 (ahead of all pending items).
    /// Has no effect if the item is already active or completed.
    /// </summary>
    private IActionResult HandleQueueMoveTop(JsonObject args, int? tag)
    {
        var requestedIds = GetRequestedTorrentIds(args);
        if (requestedIds != null)
        {
            foreach (var id in requestedIds)
            {
                if (_torrents.TryGetValue(id, out var entry))
                    _seedrManager.ReorderQueue(entry.QueueId, 0);
            }
        }

        return Ok(SuccessResponse("queue-move-top", new JsonObject(), tag));
    }

    private IActionResult HandleSessionGet(int? tag)
    {
        var downloadDir = Plugin.Instance!.Configuration.TransmissionDownloadPath ?? string.Empty;
        return Ok(SuccessResponse("session-get", new JsonObject
        {
            // version string must match Radarr's minimum version check (>= 2.40).
            // We advertise 4.0.0 so that Radarr also enables label (category) support.
            ["version"]             = "4.0.0 (JellySeedr)",
            ["download-dir"]        = downloadDir,
            ["rpc-version"]         = 17,
            ["rpc-version-minimum"] = 14,
            ["encryption"]          = "preferred",
            ["seedRatioLimit"]      = 0.0,
            ["seedRatioLimited"]    = false,
            ["idle-seeding-limit"]          = 0,
            ["idle-seeding-limit-enabled"]  = false
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
            ["torrentCount"]       = _torrents.Count
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
            InputType            = inputType,
            Source               = source ?? string.Empty,
            TorrentBytes         = torrentBytes!,
            DestinationPath      = downloadPath,
            DeleteAfterDownload  = config.DeleteAfterDownload,
            DownloadExtensions   = config.DownloadFileTypes,
            ClashResolution      = FetchNameClashResolution.Rename,
            DownloadAll          = true,
            UseSubfolderStructure = true
        };

    /// <summary>
    /// Parses a "labels" JSON array from a request arguments object.
    /// Returns an empty list when the field is absent or empty.
    /// </summary>
    private static List<string> ParseLabels(JsonObject args)
    {
        if (args["labels"] is not JsonArray arr) return new List<string>();
        return arr
            .Select(n => n?.GetValue<string>())
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s!)
            .ToList();
    }

    /// <summary>Resolves "ids" from a torrent-get/set/remove/queue-move-top request to a set of torrent IDs.</summary>
    /// <remarks>
    /// Per spec, "ids" may be:
    ///   - absent                   → all torrents (returns null)
    ///   - "recently-active"        → all torrents (returns null)
    ///   - an integer torrent id
    ///   - a SHA1 hash string
    ///   - a JSON array of any mix of the above
    /// </remarks>
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

        // Build labels JSON array from the stored label list.
        var labelsArray = new JsonArray();
        foreach (var label in entry.Labels)
            labelsArray.Add(JsonValue.Create(label));

        return new JsonObject
        {
            ["id"]                = id,
            ["name"]              = entry.Name,
            ["hashString"]        = entry.HashString,
            ["downloadDir"]       = entry.DownloadPath,
            ["status"]            = status,
            ["percentDone"]       = percentDone,
            ["totalSize"]         = totalSize,
            ["leftUntilDone"]     = leftUntilDone,
            ["isFinished"]        = isFinished,
            ["eta"]               = -1,
            ["rateDownload"]      = 0,
            ["rateUpload"]        = 0,
            ["uploadRatio"]       = 0,     // meets seedRatioLimit of 0 → Radarr triggers cleanup
            ["error"]             = queue?.Status is QueuedTorrentStatus.Failed or QueuedTorrentStatus.Cancelled ? 3 : 0,
            ["errorString"]       = queue?.ErrorMessage ?? string.Empty,
            ["secondsDownloading"] = secondsElapsed,
            ["secondsSeeding"]    = isFinished ? secondsElapsed : 0,
            ["uploadedEver"]      = 0,
            ["downloadedEver"]    = totalSize - leftUntilDone,
            // Seed* fields: fixed values that tell Radarr the torrent is immediately
            // eligible for cleanup (ratio 0 met, mode 1 = per-torrent limit).
            // Seedr manages all seeding server-side so there is nothing to configure.
            ["seedRatioLimit"]    = 0,
            ["seedRatioMode"]     = 1,
            ["seedIdleLimit"]     = 0,
            ["seedIdleMode"]      = 1,
            // file-count: both field names Radarr requests ("fileCount" for Vuze, "file-count" for Transmission)
            ["fileCount"]         = 1,
            ["file-count"]        = 1,
            ["labels"]            = labelsArray
        };
    }

    private static int MapStatus(QueuedTorrent? q) => q?.Status switch
    {
        QueuedTorrentStatus.Queued    => 3,   // queued to download
        QueuedTorrentStatus.Active    => 4,   // downloading
        QueuedTorrentStatus.Completed => 6,   // seeding
        _                             => 0    // stopped
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

    // Labels managed via torrent-add and torrent-set.
    public List<string> Labels { get; set; } = new();
}
