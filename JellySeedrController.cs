using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using JellySeedr.Configuration;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Seedrcc;

namespace JellySeedr.Api;

[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("jellyseedr")]
public class JellySeedrController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<JellySeedrController> _logger;
    private readonly PluginConfiguration _config;
    private static readonly SeedrManager _seedrManager = SeedrManager.Instance;

    public JellySeedrController(IHttpClientFactory httpClientFactory, ILibraryManager libraryManager, ILogger<JellySeedrController> logger)
    {
        _httpClientFactory = httpClientFactory;
        _libraryManager = libraryManager;
        _logger = logger;
        _config = Plugin.Instance!.Configuration;
        _seedrManager._logger ??= logger;
    }

    // -------------------------------------------------------------------------
    // Status & auth
    // -------------------------------------------------------------------------

    [HttpGet("libraries")]
    public ActionResult<IEnumerable<VirtualFolderInfo>> GetLibraries() =>
        Ok(_libraryManager.GetVirtualFolders(true));

    [HttpGet("status")]
    public async Task<IActionResult> GetSeedrStatus()
    {
        var client = await _seedrManager.EnsureClientAsync(_httpClientFactory);
        if (client == null)
            return Ok(new { loggedIn = false, username = (string?)null, storage = new { totalBytes = 0L, usedBytes = 0L } });

        try
        {
            var info = await client.GetSettingsAsync();
            return Ok(new { loggedIn = true, username = info.Account.Username, storage = new { totalBytes = info.Account.SpaceMax, usedBytes = info.Account.SpaceUsed } });
        }
        catch (Exception ex)
        {
            return BadRequest(new { loggedIn = false, message = "Error fetching Seedr info: " + ex.Message });
        }
    }

    [HttpPost("login")]
    public IActionResult SeedrLogin([FromForm] string username, [FromForm] string password, [FromForm] string? saveCredentials = null)
    {
        if (string.IsNullOrEmpty(password))
            return BadRequest(new { message = "password is required" });

        try
        {
            var result = SeedrClient.FromPasswordAsync(username, password, OnRefreshToken, _httpClientFactory.CreateClient()).Result;
            if (result == null) return BadRequest(new { message = "Login failed, no token received." });

            _seedrManager.Client = result;
            OnRefreshToken(result.Token);

            bool save = saveCredentials is "true" or "on" or "1";
            if (save) Plugin.Instance!.SaveCredentials(username, password);
            else Plugin.Instance!.DeleteCredentials();

            return Ok(new { message = "Login successful" });
        }
        catch (AuthenticationException) { return BadRequest(new { message = "Invalid username or password." }); }
        catch (Exception ex) { return BadRequest(new { message = "Error during login: " + ex.Message }); }
    }

    [HttpPost("logout")]
    public IActionResult SeedrLogout()
    {
        _seedrManager.Client = null;
        Plugin.Instance!.SaveSeedrToken(null);
        Plugin.Instance!.DeleteCredentials();
        return Ok(new { message = "Logged out" });
    }

    [HttpPost("testarr")]
    public async Task<IActionResult> TestArrConnection([FromForm] string url, [FromForm] string apiKey)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(apiKey))
            return BadRequest(new { message = "URL and API Key are required." });

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            var reqUrl = url.TrimEnd('/') + "/api/v3/system/status";
            
            var request = new HttpRequestMessage(HttpMethod.Get, reqUrl);
            request.Headers.Add("X-Api-Key", apiKey.Trim());

            var response = await client.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                return Ok(new { message = "Success" });
            }
            
            return BadRequest(new { message = $"Failed with status code: {response.StatusCode}" });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    // -------------------------------------------------------------------------
    // Torrent submission
    // -------------------------------------------------------------------------

    [HttpPost("submit")]
    public async Task<IActionResult> SeedrSubmit([FromForm] string? input, [FromForm] IFormFile? torrentFile, [FromForm] string destinationPath)
    {
        if (_seedrManager.Client == null)
            return Unauthorized(new { message = "Not logged in to Seedr" });

        if (!IsValidLibraryPath(destinationPath))
            return BadRequest(new { message = "Invalid destination path" });

        var type = DetectSeedrInput(input, torrentFile);
        var param = new SeedrTorrentAddParam
        {
            InputType = type,
            DeleteAfterDownload = _config.DeleteAfterDownload,
            DownloadExtensions = _config.DownloadFileTypes,
            DestinationPath = destinationPath,
            ClashResolution = GetClashResolution(),
            UseSubfolderStructure = _config.UseSubfolderStructure
        };

        try
        {
            switch (type)
            {
                case SeedrInputType.TorrentFile when torrentFile?.Length > 0:
                    using (var ms = new MemoryStream()) { await torrentFile.CopyToAsync(ms); param.TorrentBytes = ms.ToArray(); }
                    break;
                case SeedrInputType.TorrentUrl:
                case SeedrInputType.MagnetLink:
                    param.Source = input ?? string.Empty;
                    break;
                default:
                    return BadRequest(new { message = "Unable to determine input type. Provide a torrent file, a .torrent/http URL or a magnet link." });
            }

            var displayName = ResolveDisplayName(type, input, torrentFile);
            var (position, queueId, msg) = _seedrManager.EnqueueTorrent(_seedrManager.Client, param, displayName);
            return Ok(new { message = msg, queueId, position });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in SeedrSubmit.");
            return BadRequest(new { message = ex.Message });
        }
    }

    // -------------------------------------------------------------------------
    // File browser
    // -------------------------------------------------------------------------

    [HttpGet("seedr-browser.js")]
    [AllowAnonymous]
    public IActionResult GetBrowserScript()
    {
        const string resourceName = "JellySeedr.Configuration.SeedrBrowser.js";
        using var stream = typeof(JellySeedrController).Assembly.GetManifestResourceStream(resourceName);
        if (stream == null) return NotFound();
        using var reader = new StreamReader(stream);
        return Content(reader.ReadToEnd(), "application/javascript");
    }

    [HttpGet("contents")]
    public async Task<IActionResult> GetContents([FromQuery] string folderId = "0")
    {
        if (_seedrManager.Client == null) return Unauthorized(new { message = "Not logged in to Seedr" });
        try { return Ok(await _seedrManager.LoadFolderNodeAsync(_seedrManager.Client, folderId)); }
        catch (Exception ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("delete")]
    public async Task<IActionResult> DeleteSeedrFiles([FromBody] SeedrSelectionRequest request)
    {
        if (_seedrManager.Client == null) return Unauthorized(new { message = "Not logged in to Seedr" });
        try
        {
            var (code, message) = await _seedrManager.DeleteSelection(_seedrManager.Client, request);
            return code == 200 ? Ok(new { message }) : BadRequest(new { message });
        }
        catch (Exception ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("fetch")]
    public async Task<IActionResult> FetchSeederFile([FromBody] SeedrSelectionRequest request)
    {
        if (_seedrManager.Client == null) return Unauthorized(new { message = "Not logged in to Seedr" });
        if (!IsValidLibraryPath(request.DestinationPath)) return BadRequest(new { message = "Invalid library path." });
        try
        {
            var (code, message) = await _seedrManager.FetchFiles(_seedrManager.Client, request, GetClashResolution());
            return code == 200 ? Ok(new { message }) : BadRequest(new { message });
        }
        catch (Exception ex) { return BadRequest(new { message = ex.Message }); }
    }

    // -------------------------------------------------------------------------
    // Task management
    // -------------------------------------------------------------------------

    [HttpGet("tasks")]
    public IActionResult GetActiveTasks() => Ok(_seedrManager.GetActiveTasks());

    [HttpPost("tasks/remove")]
    public IActionResult RemoveTask([FromForm] uint taskId) =>
        _seedrManager.RemoveTask(taskId) ? Ok(new { message = "Task removed successfully" }) : NotFound(new { message = "Task not found" });

    [HttpPost("tasks/cancel")]
    public async Task<IActionResult> CancelTask([FromForm] uint taskId)
    {
        var cancelled = await _seedrManager.CancelTaskAsync(_seedrManager.Client, taskId);
        return cancelled ? Ok(new { message = "Task cancelled successfully" }) : NotFound(new { message = "Task not found or cannot be cancelled" });
    }

    [HttpPost("tasks/clear-completed")]
    public IActionResult ClearCompletedTasks() { _seedrManager.ClearCompletedTasks(); return Ok(new { message = "Completed tasks cleared successfully" }); }

    // -------------------------------------------------------------------------
    // Queue management
    // -------------------------------------------------------------------------

    [HttpGet("queue")]
    public IActionResult GetQueue() => Ok(_seedrManager.GetQueue());

    [HttpPost("queue/cancel")]
    public async Task<IActionResult> CancelQueueItem([FromForm] uint queueId)
    {
        var cancelled = await _seedrManager.CancelQueueItemAsync(queueId);
        return cancelled ? Ok(new { message = "Item cancelled" }) : BadRequest(new { message = "Item not found or can no longer be cancelled" });
    }

    [HttpPost("queue/remove")]
    public IActionResult RemoveFromQueue([FromForm] uint queueId)
    {
        var removed = _seedrManager.RemoveFromQueue(queueId);
        return removed ? Ok(new { message = "Removed from queue" }) : BadRequest(new { message = "Item not found or is currently active" });
    }

    [HttpPost("queue/reorder")]
    public IActionResult ReorderQueue([FromForm] uint queueId, [FromForm] int newPosition)
    {
        var reordered = _seedrManager.ReorderQueue(queueId, newPosition);
        return reordered ? Ok(new { message = "Queue reordered" }) : BadRequest(new { message = "Item not found or cannot be reordered" });
    }

    [HttpPost("queue/clear")]
    public IActionResult ClearCompletedQueueItems() { _seedrManager.ClearCompletedQueueItems(); return Ok(new { message = "Completed queue items cleared" }); }

    // -------------------------------------------------------------------------
    // Transmission token
    // -------------------------------------------------------------------------

    [HttpPost("transmission-token/regenerate")]
    public IActionResult RegenerateTransmissionToken() => Ok(new { token = Plugin.Instance!.RegenerateTransmissionToken() });

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private void OnRefreshToken(Token t) => Plugin.Instance!.SaveSeedrToken(t.ToBase64());

    private bool IsValidLibraryPath(string? path) =>
        !string.IsNullOrEmpty(path) &&
        _libraryManager.GetVirtualFolders(true).Any(v => v.Locations.Contains(path));

    private FetchNameClashResolution GetClashResolution()
    {
        Enum.TryParse<FetchNameClashResolution>(_config.NameClashResolution, ignoreCase: true, out var res);
        return res;
    }

    private static SeedrInputType DetectSeedrInput(string? input, IFormFile? file)
    {
        if (file?.Length > 0) return SeedrInputType.TorrentFile;
        if (string.IsNullOrEmpty(input)) return SeedrInputType.Unknown;
        input = input.Trim();
        if (input.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) return SeedrInputType.MagnetLink;
        if (input.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)) return SeedrInputType.TorrentUrl;
        if (input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            input.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return SeedrInputType.TorrentUrl;
        return SeedrInputType.Unknown;
    }

    private static string ResolveDisplayName(SeedrInputType type, string? input, IFormFile? torrentFile)
    {
        if (type == SeedrInputType.MagnetLink && !string.IsNullOrEmpty(input))
        {
            var m = System.Text.RegularExpressions.Regex.Match(input, @"dn=([^&]+)");
            return m.Success ? System.Net.WebUtility.UrlDecode(m.Groups[1].Value) : "Magnet Link";
        }
        if (type == SeedrInputType.TorrentFile) return torrentFile?.FileName ?? "torrent file";
        return input ?? "unknown";
    }
}
