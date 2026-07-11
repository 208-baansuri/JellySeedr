
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.IO;
using System.Threading.Tasks;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using JellySeedr.Configuration;
using Seedrcc;
using Microsoft.Extensions.Logging;
using System.Linq;

namespace JellySeedr.Api;

[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("jellyseedr")]
public class JellySeedrController : ControllerBase
{


    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILibraryManager _libraryManager;
    private PluginConfiguration? config;

    private static SeedrClient? seedrccClient;

    private static SeedrManager seedrManager = new SeedrManager();

    private ILogger<JellySeedrController> _logger;

    public JellySeedrController(IHttpClientFactory httpClientFactory, ILibraryManager libraryManager, ILogger<JellySeedrController> logger)
    {
        _httpClientFactory = httpClientFactory;
        _libraryManager = libraryManager;
        _logger = logger;
        config = Plugin.Instance!.Configuration;
        if (seedrManager._logger == null)
        {
            seedrManager._logger = _logger;
        }
    }

    [HttpGet]
    [Route("libraries")]
    public ActionResult<IEnumerable<VirtualFolderInfo>> GetLibraries()
    {
        return Ok(_libraryManager.GetVirtualFolders(true));
    }


    [HttpGet]
    [Route("status")]
    public async Task<IActionResult> GetSeedrStatus()
    {
        if (seedrccClient != null)
        {
            return await getSeedrUserInfoAsync(seedrccClient);
        }
        var seedrTokenStr = Plugin.Instance!.GetSeedrToken();
        var tokenAvailable = !string.IsNullOrEmpty(seedrTokenStr);
        if (tokenAvailable)
        {
            Token seedrToken = Token.FromBase64(seedrTokenStr!);
            var client = new SeedrClient(seedrToken, onRefreshSeedrToken, _httpClientFactory.CreateClient());
            var result = await getSeedrUserInfoAsync(client);
            if (result is OkObjectResult)
            {
                seedrccClient = client;
                return result;
            }

            // If we can't get user info, the token is probably invalid. Clear it.
            Plugin.Instance!.SaveSeedrToken(null);
        }

        // Token didn't work or isn't available. Check if credentials file exists!
        var savedCreds = Plugin.Instance!.LoadCredentials();
        if (savedCreds != null)
        {
            try
            {
                var client = await SeedrClient.FromPasswordAsync(savedCreds.Value.username, savedCreds.Value.password, onRefreshSeedrToken, _httpClientFactory.CreateClient());
                if (client != null)
                {
                    seedrccClient = client;
                    var token = seedrccClient.Token;
                    onRefreshSeedrToken(token); // Save the new token
                    return await getSeedrUserInfoAsync(seedrccClient);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to login to Seedr with saved credentials.");
            }
        }

        return Ok(new
        {
            loggedIn = false,
            username = (string?)null,
            storage = new { totalBytes = 0L, usedBytes = 0L }
        });
    }

    private async Task<IActionResult> getSeedrUserInfoAsync(SeedrClient client)
    {
        try
        {
            var userInfo = await client.GetSettingsAsync();

            return Ok(new
            {
                loggedIn = true,
                username = userInfo.Account.Username,
                storage = new { totalBytes = userInfo.Account.SpaceMax, usedBytes = userInfo.Account.SpaceUsed }
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new
            {
                loggedIn = false,
                message = "Error fetching Seedr info: " + ex.Message
            }
            );
        }
    }

    private void onRefreshSeedrToken(Token newToken)
    {
        Plugin.Instance!.SaveSeedrToken(newToken.ToBase64());
    }

    [HttpPost]
    [Route("login")]
    public IActionResult SeedrLogin([FromForm] string username, [FromForm] string password, [FromForm] string? saveCredentials = null)
    {
        if (string.IsNullOrEmpty(password))
        {
            return BadRequest(new { message = "password is required" });
        }

        try
        {
            var result = SeedrClient.FromPasswordAsync(username, password, onRefreshSeedrToken, _httpClientFactory.CreateClient()).Result;
            if (result == null)
            {
                return BadRequest(new { message = "Login failed, no token received." });
            }
            seedrccClient = result;
            var token = seedrccClient.Token;
            onRefreshSeedrToken(token); // Persist the token

            bool shouldSave = saveCredentials == "true" || saveCredentials == "on" || saveCredentials == "1";
            if (shouldSave)
            {
                Plugin.Instance!.SaveCredentials(username, password);
            }
            else
            {
                Plugin.Instance!.DeleteCredentials();
            }

            return Ok(new { message = "Login successful" });
        }
        catch (AuthenticationException)
        {
            return BadRequest(new { message = "Invalid username or password." });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = "Error during login: " + ex.Message });
        }
    }

    [HttpPost]
    [Route("logout")]
    public IActionResult SeedrLogout()
    {
        seedrccClient = null;
        Plugin.Instance!.SaveSeedrToken(null);
        Plugin.Instance!.DeleteCredentials();
        return Ok(new { message = "Logged out" });
    }

    private SeedrInputType DetectSeedrInput(string? input, IFormFile? file)
    {
        if (file != null && file.Length > 0) return SeedrInputType.TorrentFile;
        if (string.IsNullOrEmpty(input)) return SeedrInputType.Unknown;

        input = input.Trim();

        if (input.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) return SeedrInputType.MagnetLink;
        if (input.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)) return SeedrInputType.TorrentUrl;

        // Treat http/https links as torrent URLs (could be a .torrent or a redirect)
        if (input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return SeedrInputType.TorrentUrl;

        return SeedrInputType.Unknown;
    }

    [HttpPost]
    [Route("submit")]
    public async Task<IActionResult> SeedrSubmit([FromForm] string? input, [FromForm] IFormFile? torrentFile, [FromForm] string destinationPath)
    {
        if (seedrccClient == null)
        {
            _logger.LogWarning("SeedrSubmit failed: Not logged in to Seedr.");
            return Unauthorized(new { message = "Not logged in to Seedr" });
        }

        if (!isValidLibraryPath(destinationPath))
        {
            _logger.LogWarning("SeedrSubmit failed: Invalid destination path '{DestinationPath}'", destinationPath);
            return BadRequest(new { message = "Invalid destination path" });
        }

        var type = DetectSeedrInput(input, torrentFile);

        var seedrTorrentAddParam = new SeedrTorrentAddParam
        {
            InputType = type,
            DeleteAfterDownload = config?.DeleteAfterDownload ?? true,
            DownloadExtensions = config?.DownloadFileTypes ?? new HashSet<string> { },
            DestinationPath = destinationPath,
            ClashResolution = GetFetchNameClashResolution()
        };

        try
        {
            switch (type)
            {
                case SeedrInputType.TorrentFile:
                    {
                        if (torrentFile != null && torrentFile.Length > 0)
                        {
                            using (var ms = new MemoryStream())
                            {
                                await torrentFile.CopyToAsync(ms);
                                seedrTorrentAddParam.TorrentBytes = ms.ToArray();
                            }
                        }
                        break;
                    }

                case SeedrInputType.TorrentUrl:
                case SeedrInputType.MagnetLink:
                    {
                        seedrTorrentAddParam.Source = input ?? string.Empty;
                        break;
                    }
                default:
                    _logger.LogWarning("SeedrSubmit failed: Unable to determine input type.");
                    return BadRequest(new { message = "Unable to determine input type. Provide a torrent file, a .torrent/http URL or a magnet link." });
            }

            string displayName;
            if (type == SeedrInputType.MagnetLink && !string.IsNullOrEmpty(input))
            {
                var match = System.Text.RegularExpressions.Regex.Match(input, @"dn=([^&]+)");
                displayName = match.Success
                    ? System.Net.WebUtility.UrlDecode(match.Groups[1].Value)
                    : "Magnet Link";
            }
            else if (type == SeedrInputType.TorrentFile)
            {
                displayName = torrentFile?.FileName ?? "torrent file";
            }
            else
            {
                displayName = input ?? "unknown";
            }

            var (position, queueId, msg) = seedrManager.EnqueueTorrent(seedrccClient, seedrTorrentAddParam, displayName);
            return Ok(new { message = msg, queueId, position });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception occurred during SeedrSubmit execution.");
            return BadRequest(new { message = ex.Message });
        }
    }


    [HttpGet]
    [Route("seedr-browser.js")]
    [AllowAnonymous]
    public IActionResult GetBrowserScript()
    {
        const string resourceName = "JellySeedr.Configuration.SeedrBrowser.js";
        var assembly = typeof(JellySeedrController).Assembly;

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            return NotFound();
        }

        using var reader = new StreamReader(stream);
        return Content(reader.ReadToEnd(), "application/javascript");
    }


    [HttpGet]
    [Route("contents")]
    public async Task<IActionResult> GetContents([FromQuery] string folderId = "0")
    {
        if (seedrccClient == null)
        {
            return Unauthorized(new { message = "Not logged in to Seedr" });
        }

        try
        {
            var root = await seedrManager.LoadFolderNodeAsync(seedrccClient, folderId);
            return Ok(root);
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }


    [HttpPost]
    [Route("delete")]
    public async Task<IActionResult> DeleteSeedrFiles([FromBody] SeedrSelectionRequest request)
    {
        if (seedrccClient == null)
        {
            return Unauthorized(new { message = "Not logged in to Seedr" });
        }

        try
        {
            var (statusCode, message) = await seedrManager.DeleteSelection(seedrccClient, request);
            if (statusCode == 200)
            {
                return Ok(new { message });
            }
            else
            {
                return BadRequest(new { message });
            }
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost]
    [Route("fetch")]
    public async Task<IActionResult> FetchSeederFile([FromBody] SeedrSelectionRequest request)
    {
        if (seedrccClient == null)
        {
            return Unauthorized(new { message = "Not logged in to Seedr" });
        }
        try
        {
            if (!isValidLibraryPath(request.DestinationPath))
            {
                return BadRequest(new { message = "Invalid library path." });
            }

            var (statusCode, message) = await seedrManager.FetchFiles(seedrccClient, request, GetFetchNameClashResolution());
            return statusCode == 200 ? Ok(new { message }) : BadRequest(new { message });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }


    [HttpGet]
    [Route("tasks")]
    public IActionResult GetActiveTasks()
    {
        return Ok(seedrManager.GetActiveTasks());
    }

    [HttpPost]
    [Route("tasks/remove")]
    public IActionResult RemoveTask([FromForm] uint taskId)
    {
        var removed = seedrManager.RemoveTask(taskId);
        if (removed)
        {
            return Ok(new { message = "Task removed successfully" });
        }
        return NotFound(new { message = "Task not found" });
    }

    [HttpPost]
    [Route("tasks/cancel")]
    public async Task<IActionResult> CancelTask([FromForm] uint taskId)
    {
        var cancelled = await seedrManager.CancelTaskAsync(seedrccClient, taskId);
        if (cancelled)
        {
            return Ok(new { message = "Task cancelled successfully" });
        }
        return NotFound(new { message = "Task not found or cannot be cancelled" });
    }

    [HttpPost]
    [Route("tasks/clear-completed")]
    public IActionResult ClearCompletedTasks()
    {
        seedrManager.ClearCompletedTasks();
        return Ok(new { message = "Completed tasks cleared successfully" });
    }

    [HttpGet]
    [Route("queue")]
    public IActionResult GetQueue()
    {
        return Ok(seedrManager.GetQueue());
    }

    [HttpPost]
    [Route("queue/cancel")]
    public async Task<IActionResult> CancelQueueItem([FromForm] uint queueId)
    {
        var cancelled = await seedrManager.CancelQueueItemAsync(queueId);
        if (cancelled)
        {
            return Ok(new { message = "Item cancelled" });
        }
        return BadRequest(new { message = "Item not found or can no longer be cancelled" });
    }

    [HttpPost]
    [Route("queue/remove")]
    public IActionResult RemoveFromQueue([FromForm] uint queueId)
    {
        var removed = seedrManager.RemoveFromQueue(queueId);
        if (removed)
        {
            return Ok(new { message = "Removed from queue" });
        }
        return BadRequest(new { message = "Item not found or is currently active" });
    }

    [HttpPost]
    [Route("queue/reorder")]
    public IActionResult ReorderQueue([FromForm] uint queueId, [FromForm] int newPosition)
    {
        var reordered = seedrManager.ReorderQueue(queueId, newPosition);
        if (reordered)
        {
            return Ok(new { message = "Queue reordered" });
        }
        return BadRequest(new { message = "Item not found or cannot be reordered" });
    }

    [HttpPost]
    [Route("queue/clear")]
    public IActionResult ClearCompletedQueueItems()
    {
        seedrManager.ClearCompletedQueueItems();
        return Ok(new { message = "Completed queue items cleared" });
    }


    private bool isValidLibraryPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var isValid = _libraryManager.GetVirtualFolders(true).Any(v => v.Locations.Contains(path));
        return isValid;
    }

    private FetchNameClashResolution GetFetchNameClashResolution()
    {
        FetchNameClashResolution res = FetchNameClashResolution.Rename;
        Enum.TryParse<FetchNameClashResolution>(config!.NameClashResolution, true, out res);
        return res;
    }

}
