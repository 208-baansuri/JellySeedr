
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
        if (seedrManager.Logger == null)
        {
            seedrManager.Logger = _logger;
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
        if (seedrccClient != null) {
            return await getSeedrUserInfoAsync(seedrccClient);
        }
        var tokenAvailable = config != null && !string.IsNullOrEmpty(config.SeedrToken);
        if (tokenAvailable)
        {
            Token seedrToken = Token.FromBase64(config!.SeedrToken!);
            var client = new SeedrClient(seedrToken, onRefreshSeedrToken, _httpClientFactory.CreateClient());
            var result = await getSeedrUserInfoAsync(client);
            if (!(result is OkObjectResult)) {
                // If we can't get user info, the token is probably invalid. Clear it from config.
                config!.SeedrToken = null;
                Plugin.Instance!.SaveConfiguration();
                return BadRequest(new { message = "Invalid Seedr token, please log in again." });
            }
            seedrccClient = client;
            return result;
        }
        else
        {
            return Ok(new
            {
                loggedIn = false,
                username = (string?)null,
                storage = new { totalBytes = 0L, usedBytes = 0L }
            });
        }
    }

    private async Task<IActionResult> getSeedrUserInfoAsync(SeedrClient client)
    {
        try {
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
            return BadRequest(new { 
                loggedIn = false, 
                message = "Error fetching Seedr info: " + ex.Message }
            );
        }
    }

    private void onRefreshSeedrToken(Token newToken)
    {
        if (config != null)
        {
            config.SeedrToken = newToken.ToBase64();
            Plugin.Instance!.SaveConfiguration();
        }
    }

    [HttpPost]
    [Route("login")]
    public IActionResult SeedrLogin([FromForm] string username, [FromForm] string password)
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
                onRefreshSeedrToken(token); // Persist the token in config
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
        if (config != null)
        {
            config.SeedrToken = null;
            Plugin.Instance!.SaveConfiguration();
        }
        return Ok(new { message = "Logged out" });
    }


    private enum SeedrInputType { Unknown, TorrentFile, TorrentUrl, MagnetLink }

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
    public async Task<IActionResult> SeedrSubmit([FromForm] string? input, [FromForm] IFormFile? torrentFile)
    {
        if (seedrccClient == null)
        {
            return Unauthorized(new { message = "Not logged in to Seedr" });
        }

        var type = DetectSeedrInput(input, torrentFile);

        try
        {
            switch (type)
            {
                case SeedrInputType.TorrentFile:
                {
                    // Save temporary file and submit to Seedr (integration point)
                    var tempPath = Path.GetTempFileName();
                    using (var fs = System.IO.File.Create(tempPath))
                    {
                        await torrentFile!.CopyToAsync(fs);
                    }

                    // TODO: call Seedrcc wrapper to upload the torrent file using config.SeedrToken
                    // Example (pseudocode): SeedrClient.AddTorrentFromFile(config.SeedrToken, tempPath);

                    System.IO.File.Delete(tempPath);
                    return Ok(new { message = "Torrent file received and submitted (stub)" });
                }

                case SeedrInputType.TorrentUrl:
                    // TODO: call Seedrcc wrapper to add torrent from URL
                    // Example (pseudocode): SeedrClient.AddTorrentFromUrl(config.SeedrToken, input);
                    return Ok(new { message = "Torrent URL submitted (stub)", url = input });

                case SeedrInputType.MagnetLink:
                    // TODO: call Seedrcc wrapper to add magnet link
                    // Example (pseudocode): SeedrClient.AddMagnet(config.SeedrToken, input);
                    return Ok(new { message = "Magnet link submitted (stub)", magnet = input });

                default:
                    return BadRequest(new { message = "Unable to determine input type. Provide a torrent file, a .torrent/http URL or a magnet link." });
            }
        }
        catch (Exception ex)
        {
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
            var (statusCode, message) = await seedrManager.FetchFiles(seedrccClient, request);
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

}
