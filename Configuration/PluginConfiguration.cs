using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;

namespace JellySeedr.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{

    public PluginConfiguration() { }

    // Radarr / Sonarr settings
    public string RadarrUrl { get; set; } = "http://localhost:7878";
    public string RadarrApiKey { get; set; } = string.Empty;
    public bool AutoDeleteFailedRadarrDownloads { get; set; } = false;

    public string SonarrUrl { get; set; } = "http://localhost:8989";
    public string SonarrApiKey { get; set; } = string.Empty;
    public bool AutoDeleteFailedSonarrDownloads { get; set; } = false;

    /// <summary>
    /// The selected Jellyfin library.
    /// </summary>
    public string? SelectedLibrary { get; set; }

    /// <summary>
    /// Store the file types to download from Seedr
    /// </summary>
    public HashSet<string> DownloadFileTypes { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "mkv", "mov", "webm", "m4v", "avi", "flv",
        "srt", "sub"
    };

    /// <summary>
    /// Gets or sets a value indicating whether to delete files from Seedr after downloading.
    /// </summary>
    public bool DeleteAfterDownload { get; set; } = true;

    /// <summary>
    /// Gets or sets the resolution mode for naming clashes.
    /// </summary>
    public string NameClashResolution { get; set; } = "Rename";

    /// <summary>
    /// Gets or sets a value indicating whether to auto-download matching files when a torrent completes.
    /// </summary>
    public bool AutoDownload { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether downloaded files are placed in a subfolder
    /// named after the torrent (destinationPath/TorrentName/files). Enabled by default.
    /// </summary>
    public bool UseSubfolderStructure { get; set; } = true;

    /// <summary>
    /// Gets or sets how many queued torrents are processed at the same time.
    /// Seedr free accounts only support 1 concurrent torrent transfer.
    /// </summary>
    public int MaxConcurrentTorrents { get; set; } = 1;

    /// <summary>
    /// Gets or sets the secret token used to authenticate the mock Transmission API via HTTP Basic Auth (as password).
    /// </summary>
    public string TransmissionToken { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the filesystem path where torrents added via the mock Transmission API are downloaded.
    /// </summary>
    public string? TransmissionDownloadPath { get; set; }

}