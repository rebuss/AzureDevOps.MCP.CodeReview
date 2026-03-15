using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace REBUSS.Pure.AzureDevOpsIntegration.Configuration;

/// <summary>
/// Stores cached configuration in a JSON file under the user's local application data folder.
/// Location: <c>%LOCALAPPDATA%/REBUSS.Pure/config.json</c> (Windows)
/// or <c>~/.local/share/REBUSS.Pure/config.json</c> (Linux/macOS).
/// </summary>
public class LocalConfigStore : ILocalConfigStore
{
    private static readonly string ConfigDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "REBUSS.Pure");

    private static readonly string ConfigFilePath = Path.Combine(ConfigDirectory, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ILogger<LocalConfigStore> _logger;

    public LocalConfigStore(ILogger<LocalConfigStore> logger)
    {
        _logger = logger;
    }

    public CachedConfig? Load()
    {
        try
        {
            if (!File.Exists(ConfigFilePath))
            {
                _logger.LogDebug("No cached config found at {Path}", ConfigFilePath);
                return null;
            }

            var json = File.ReadAllText(ConfigFilePath);
            var config = JsonSerializer.Deserialize<CachedConfig>(json, JsonOptions);

            _logger.LogDebug("Loaded cached config from {Path}", ConfigFilePath);
            return config;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load cached config from {Path}", ConfigFilePath);
            return null;
        }
    }

    public void Save(CachedConfig config)
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);

            var json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(ConfigFilePath, json);

            _logger.LogDebug("Saved cached config to {Path}", ConfigFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save cached config to {Path}", ConfigFilePath);
        }
    }
}
