namespace WindowsSoundboard.Models;

/// <summary>How a hotkey or click turns a sound on.</summary>
public enum TriggerMode
{
    /// <summary>Every press starts an extra copy; presses stack up.</summary>
    Overlap,

    /// <summary>A press restarts the sound, cutting whatever was playing.</summary>
    Stop,

    /// <summary>The sound plays while the key is held and stops on release.</summary>
    Hold,
}

public static class TriggerModeExtensions
{
    public static string Label(this TriggerMode mode) => mode switch
    {
        TriggerMode.Overlap => "Overlap",
        TriggerMode.Stop => "Stop & restart",
        TriggerMode.Hold => "Play & hold",
        _ => mode.ToString(),
    };

    public static string Description(this TriggerMode mode) => mode switch
    {
        TriggerMode.Overlap => "Each press layers another copy on top of the last one.",
        TriggerMode.Stop => "Each press restarts the sound from the beginning.",
        TriggerMode.Hold => "The sound plays while the key is held and stops when you let go.",
        _ => string.Empty,
    };

    public static TriggerMode Next(this TriggerMode mode) => mode switch
    {
        TriggerMode.Overlap => TriggerMode.Stop,
        TriggerMode.Stop => TriggerMode.Hold,
        _ => TriggerMode.Overlap,
    };

    public static bool IsHold(this TriggerMode mode) => mode == TriggerMode.Hold;
}
