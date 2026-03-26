using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace REBUSS.Pure.GitHub.Configuration;

/// <summary>
/// Stores cached GitHub configuration in a JSON file under the user's local application data folder.
/// Location: <c>%LOCALAPPDATA%/REBUSS.Pure/github-config.json</c> (Windows)
/// or <c>~/.local/share/REBUSS.Pure/github-config.json</c> (Linux/macOS).
/// </summary>
public class GitHubConfigStore : IGitHubConfigStore
{
    private static readonly string ConfigDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "REBUSS.Pure");

    private static readonly string ConfigFilePath = Path.Combine(ConfigDirectory, "github-config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ILogger<GitHubConfigStore> _logger;

    public GitHubConfigStore(ILogger<GitHubConfigStore> logger)
    {
        _logger = logger;
    }

    public GitHubCachedConfig? Load()
    {
        try
        {
            if (!File.Exists(ConfigFilePath))
            {
                _logger.LogDebug("No cached GitHub config found at {Path}", ConfigFilePath);
                return null;
            }

            var json = File.ReadAllText(ConfigFilePath);
            var config = JsonSerializer.Deserialize<GitHubCachedConfig>(json, JsonOptions);

            _logger.LogDebug("Loaded cached GitHub config from {Path}", ConfigFilePath);
            return config;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load cached GitHub config from {Path}", ConfigFilePath);
            return null;
        }
    }

    public void Save(GitHubCachedConfig config)
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);

            var json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(ConfigFilePath, json);

            _logger.LogDebug("Saved cached GitHub config to {Path}", ConfigFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save cached GitHub config to {Path}", ConfigFilePath);
        }
    }
}
