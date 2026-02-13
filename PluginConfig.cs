using Newtonsoft.Json;

using TShockAPI;

namespace SkipVersionCheck;

/// <summary>
/// Configuration file for SkipVersionCheck plugin.
/// Saved to tshock/SkipVersionCheck.json.
/// </summary>
public class PluginConfig
{
    /// <summary>
    /// Enable verbose debug logging for packet-level diagnostics.
    /// Default: false.
    /// </summary>
    [JsonProperty("DebugLogging")]
    public bool DebugLogging { get; set; } = false;

    /// <summary>
    /// Minimum supported client release number.
    /// Clients below this version will be rejected.
    /// Default: 315 (v1.4.5.0).
    /// </summary>
    [JsonProperty("MinSupportedRelease")]
    public int MinSupportedRelease { get; set; } = 315;

    private static string ConfigPath => Path.Combine(TShock.SavePath, "SkipVersionCheck.json");

    public static PluginConfig Load()
    {
        if (File.Exists(ConfigPath))
        {
            try
            {
                var config = JsonConvert.DeserializeObject<PluginConfig>(
                    File.ReadAllText(ConfigPath)) ?? new PluginConfig();
                // Re-save to pick up any new fields
                config.Save();
                return config;
            }
            catch (Exception ex)
            {
                TShock.Log.ConsoleError(
                    $"[SkipVersionCheck] Error loading config: {ex.Message}. Using defaults.");
            }
        }

        var defaultConfig = new PluginConfig();
        defaultConfig.Save();
        return defaultConfig;
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(ConfigPath,
                JsonConvert.SerializeObject(this, Formatting.Indented));
        }
        catch (Exception ex)
        {
            TShock.Log.ConsoleError(
                $"[SkipVersionCheck] Error saving config: {ex.Message}");
        }
    }
}
