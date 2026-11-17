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

    /// <summary>How a press or hotkey starts a sound.</summary>
    public TriggerMode Mode { get; set; } = TriggerMode.Stop;

    /// <summary>Fallback play mode for sounds that do not set their own.</summary>
    public PlayMode PlayMode { get; set; } = PlayMode.Default;

    /// <summary>Per-sound overrides of <see cref="PlayMode"/>, keyed by sound id.</summary>
    public Dictionary<string, PlayMode> PlayModes { get; set; } = new();

    public string MicDeviceId { get; set; } = string.Empty;

    /// <summary>Mix the chosen microphone into the cable so other apps hear the voice.</summary>
    public bool MicPassthrough { get; set; }

    /// <summary>Microphone gain, 0.0 - 2.0.</summary>
    public double MicVolume { get; set; } = 1.0;

    /// <summary>Capture buffer size; smaller is lower latency but less forgiving.</summary>
    public int MicBufferMilliseconds { get; set; } = 400;

    /// <summary>Sound id (relative path) to binding text such as "Ctrl+F1".</summary>
    public Dictionary<string, string> Hotkeys { get; set; } = new();

    /// <summary>Control action id ("stop_all", …) to binding text.</summary>
    public Dictionary<string, string> ControlHotkeys { get; set; } = new();

    /// <summary>Let several sounds answer to the same key combination.</summary>
    public bool MultiSoundHotkeys { get; set; }

    /// <summary>Which sound a shared hotkey plays.</summary>
    public GroupMode GroupMode { get; set; } = GroupMode.Same;

    /// <summary>Even out per-sound loudness on the way out.</summary>
    public bool NormaliseLoudness { get; set; } = true;

    /// <summary>Extra digital gain on top of the levelling, 0 - 30 dB.</summary>
    public double LoudnessBoostDb { get; set; }

    /// <summary>Silence the cable without touching the volume setting.</summary>
    public bool Muted { get; set; }

    public AppTheme Theme { get; set; } = AppTheme.System;

    public ListStyle ListStyle { get; set; } = ListStyle.Cards;

    /// <summary>Keep an icon in the notification area.</summary>
    public bool TrayEnabled { get; set; } = true;

    /// <summary>The close button hides the window instead of quitting.</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>Measured gains, so a file is only analysed once.</summary>
    public Dictionary<string, LoudnessCacheEntry> Loudness { get; set; } = new();

    /// <summary>The play mode a sound actually uses: its own, or the board default.</summary>
    public PlayMode ModeFor(string soundId)
        => PlayModes.TryGetValue(soundId, out var mode) ? mode : PlayMode;

    public void SetModeFor(string soundId, PlayMode? mode)
    {
        if (mode is null)
        {
            PlayModes.Remove(soundId);
            return;
        }

        PlayModes[soundId] = mode.Value;
    }

    public string? GetControlHotkey(string actionId)
        => ControlHotkeys.TryGetValue(actionId, out var text) ? text : null;

    public void SetControlHotkey(string actionId, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            ControlHotkeys.Remove(actionId);
            return;
        }

        ControlHotkeys[actionId] = text;
    }
}

public sealed class LoudnessCacheEntry
{
    public long Length { get; set; }

    public long ModifiedTicks { get; set; }

    public double GainDb { get; set; }
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

            Log.Info($"config loaded: {config.Hotkeys.Count} hotkeys, {config.ControlHotkeys.Count} control " +
                     $"hotkeys, mode {config.Mode}, default play mode {config.PlayMode}, " +
                     $"{config.PlayModes.Count} per-sound play modes, folder '{config.SoundsFolder}'");
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
