using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using System;

namespace JellySeedr.Configuration;
public class PluginConfiguration : BasePluginConfiguration
{

    public PluginConfiguration()
    {
        SeedrToken = null;
    }

    /// <summary>
    /// Storage for Seedr token, stored as Base64 string. 
    /// </summary>
    public string? SeedrToken { get; set; }

    /// <summary>
    /// The selected Jellyfin library.
    /// </summary>
    public string? selectedLibrary { get; set; }

}