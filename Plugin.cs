using System;
using System.IO;
using System.Text;
using System.Security.Cryptography;
using System.Globalization;
using System.Collections.Generic;
using JellySeedr.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace JellySeedr;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    // <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer) : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc/>
    public override string Name => "JellySeedr";

    /// <inheritdoc/>
    public override Guid Id => Guid.Parse("50be6aa0-120a-48e1-9bf1-e95a867587e0");

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    private string? GetSubdirectoryFilePath(string fileName, bool createDirectory = false)
    {
        var baseDirectory = Path.GetDirectoryName(ConfigurationFilePath);
        if (string.IsNullOrEmpty(baseDirectory)) return null;

        var targetDirectory = Path.Combine(baseDirectory, "Jellyfin.Plugin.JellySeedr");
        if (createDirectory && !Directory.Exists(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
        }

        return Path.Combine(targetDirectory, fileName);
    }

    public string? GetSeedrToken()
    {
        try
        {
            var filePath = GetSubdirectoryFilePath("token.key");
            if (filePath != null && File.Exists(filePath))
            {
                var cipherBytes = File.ReadAllBytes(filePath);
                return Decrypt(cipherBytes);
            }
        }
        catch
        {
            // Ignore
        }
        return null;
    }

    public void SaveSeedrToken(string? token)
    {
        try
        {
            var filePath = GetSubdirectoryFilePath("token.key", createDirectory: true);
            if (filePath == null) return;

            if (string.IsNullOrEmpty(token))
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            else
            {
                var cipherBytes = Encrypt(token);
                File.WriteAllBytes(filePath, cipherBytes);
            }
        }
        catch
        {
            // Ignore
        }
    }

    private static readonly byte[] CryptoKey = [0x50, 0xbe, 0x6a, 0xa0, 0x12, 0x0a, 0x48, 0xe1, 0x9b, 0xf1, 0xe9, 0x5a, 0x86, 0x75, 0x87, 0xe0];
    private static readonly byte[] CryptoIv = [0x86, 0x75, 0x87, 0xe0, 0x50, 0xbe, 0x6a, 0xa0, 0x12, 0x0a, 0x48, 0xe1, 0x9b, 0xf1, 0xe9, 0x5a];

    public void SaveCredentials(string username, string password)
    {
        try
        {
            var filePath = GetSubdirectoryFilePath("credentials.key", createDirectory: true);
            if (filePath == null) return;

            var plainText = $"{username}\n{password}";
            var cipherBytes = Encrypt(plainText);
            File.WriteAllBytes(filePath, cipherBytes);
        }
        catch
        {
            // Ignore
        }
    }

    public (string username, string password)? LoadCredentials()
    {
        try
        {
            var filePath = GetSubdirectoryFilePath("credentials.key");
            if (filePath != null && File.Exists(filePath))
            {
                var cipherBytes = File.ReadAllBytes(filePath);
                var plainText = Decrypt(cipherBytes);
                var parts = plainText.Split('\n', 2);
                if (parts.Length == 2)
                {
                    return (parts[0], parts[1]);
                }
            }
        }
        catch
        {
            // Ignore
        }
        return null;
    }

    public void DeleteCredentials()
    {
        try
        {
            var filePath = GetSubdirectoryFilePath("credentials.key");
            if (filePath != null && File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
            // Ignore
        }
    }

    private byte[] Encrypt(string plainText)
    {
        using var aes = Aes.Create();
        aes.Key = CryptoKey;
        aes.IV = CryptoIv;
        using var encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
        using var ms = new MemoryStream();
        using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
        using (var sw = new StreamWriter(cs, Encoding.UTF8))
        {
            sw.Write(plainText);
        }
        return ms.ToArray();
    }

    private string Decrypt(byte[] cipherText)
    {
        using var aes = Aes.Create();
        aes.Key = CryptoKey;
        aes.IV = CryptoIv;
        using var decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
        using var ms = new MemoryStream(cipherText);
        using var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read);
        using var sr = new StreamReader(cs, Encoding.UTF8);
        return sr.ReadToEnd();
    }

    /// <summary>
    /// Returns the current Transmission secret token, generating and persisting one if not yet set.
    /// </summary>
    public string GetOrCreateTransmissionToken()
    {
        if (string.IsNullOrEmpty(Configuration.TransmissionToken))
        {
            Configuration.TransmissionToken = Guid.NewGuid().ToString("N");
            SaveConfiguration();
        }
        return Configuration.TransmissionToken;
    }

    /// <summary>
    /// Generates a new Transmission secret token, persists it, and returns it.
    /// </summary>
    public string RegenerateTransmissionToken()
    {
        Configuration.TransmissionToken = Guid.NewGuid().ToString("N");
        SaveConfiguration();
        return Configuration.TransmissionToken;
    }

    // <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace),
                EnableInMainMenu = true
            }
        ];
    }
}