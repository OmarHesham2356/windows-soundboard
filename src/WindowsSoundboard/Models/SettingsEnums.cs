namespace WindowsSoundboard.Models;

/// <summary>What happens when a sound reaches its end.</summary>
public enum PlayMode
{
    Default,
    Loop,
    Continue,
}

public static class PlayModeExtensions
{
    public static string Label(this PlayMode mode) => mode switch
    {
        PlayMode.Default => "Default",
        PlayMode.Loop => "Loop",
        PlayMode.Continue => "Continue",
        _ => mode.ToString(),
    };

    public static string Description(this PlayMode mode) => mode switch
    {
        PlayMode.Default => "Plays the sound once, then stops.",
        PlayMode.Loop => "Repeats the sound until it is stopped.",
        PlayMode.Continue => "When a sound ends, the next one in the board starts.",
        _ => string.Empty,
    };

    public static PlayMode Next(this PlayMode mode) => mode switch
    {
        PlayMode.Default => PlayMode.Loop,
        PlayMode.Loop => PlayMode.Continue,
        _ => PlayMode.Default,
    };
}

/// <summary>Which sound a hotkey shared by several sounds plays.</summary>
public enum GroupMode
{
    Same,
    Next,
    Random,
}

public static class GroupModeExtensions
{
    public static string Label(this GroupMode mode) => mode switch
    {
        GroupMode.Same => "Play the same sound",
        GroupMode.Next => "Play the next sound",
        GroupMode.Random => "Play a random sound",
        _ => mode.ToString(),
    };
}

public enum AppTheme
{
    System,
    Dark,
    Light,
}

public enum ListStyle
{
    Cards,
    Compact,
}

/// <summary>One global control hotkey, as listed in Options → Hotkeys.</summary>
public sealed record ControlAction(string Id, string Title, string Subtitle);

public static class ControlActions
{
    public const string PlayPause = "play_pause";
    public const string StopAll = "stop_all";
    public const string PreviousSound = "previous_sound";
    public const string NextSound = "next_sound";
    public const string MuteOutput = "mute_output";
    public const string MuteMic = "mute_mic";
    public const string CycleTriggerMode = "cycle_trigger_mode";
    public const string CyclePlayMode = "cycle_play_mode";

    public static IReadOnlyList<ControlAction> All { get; } =
    [
        new(PlayPause, "Play / Pause", "Pause or resume the sound that is playing"),
        new(StopAll, "Stop All", "Stop every playing sound"),
        new(PreviousSound, "Previous Sound", "Play the previous sound in the board"),
        new(NextSound, "Next Sound", "Play the next sound in the board"),
        new(MuteOutput, "Mute Output", "Toggle what other apps hear from the cable"),
        new(MuteMic, "Mute Real Mic", "Toggle microphone passthrough"),
        new(CycleTriggerMode, "Cycle Trigger Mode", "Cycle play & overlap / play & stop / push to loop / play & hold"),
        new(CyclePlayMode, "Cycle Play Mode", "Cycle default / loop / continue"),
    ];
}
