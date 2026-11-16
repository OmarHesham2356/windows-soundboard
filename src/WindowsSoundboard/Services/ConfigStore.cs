using System.Text.Json;
using System.Text.Json.Serialization;
using WindowsSoundboard.Models;

namespace WindowsSoundboard.Services;

public sealed class AppConfig
{
    public string SoundsFolder { get; set; } = AppPaths.DefaultSoundsFolder;

    public string OutputDeviceId { get; set; } = string.Empty;

    /// <summary>Master gain, 0.0 - 1.5.</summary>
    public double Volume { get; set; } = 0.9;

    public TriggerMode Mode { get; set; } = TriggerMode.Stop;

    public string MicDeviceId { get; set; } = string.Empty;

    /// <summary>Mix the chosen microphone into the cable so other apps hear the voice.</summary>
    public bool MicPassthrough { get; set; }

    /// <summary>Sound id (relative path) to binding text such as "Ctrl+F1".</summary>
    public Dictionary<string, string> Hotkeys { get; set; } = new();
}

public static class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppConfig Load()
    {
        try
        {
            if (!File.Exists(AppPaths.ConfigFile))
            {
                return new AppConfig();
            }

            var json = File.ReadAllText(AppPaths.ConfigFile);
            var config = JsonSerializer.Deserialize<AppConfig>(json, Options);
            if (config is null)
            {
                return new AppConfig();
            }

            Log.Info($"config loaded: {config.Hotkeys.Count} hotkeys, mode {config.Mode}, folder '{config.SoundsFolder}'");
            return config;
        }
        catch (Exception ex)
        {
            Log.Error("config load failed, starting from defaults", ex);
            return new AppConfig();
        }
    }

    public static void Save(AppConfig config)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataFolder);
            File.WriteAllText(AppPaths.ConfigFile, JsonSerializer.Serialize(config, Options));
        }
        catch (Exception ex)
        {
            Log.Error("config save failed", ex);
        }
    }
}
