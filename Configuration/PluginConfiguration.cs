using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;

namespace JellySeedr.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{

    public PluginConfiguration() { }



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
    /// Gets or sets how many queued torrents are processed at the same time.
    /// Seedr free accounts only support 1 concurrent torrent transfer.
    /// </summary>
    public int MaxConcurrentTorrents { get; set; } = 1;

}