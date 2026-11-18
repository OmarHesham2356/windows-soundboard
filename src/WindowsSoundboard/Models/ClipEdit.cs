using System.Globalization;

namespace WindowsSoundboard.Models;

/// <summary>
/// A non-destructive edit of one sound: where playback starts and ends, how it
/// fades, its own gain, and how fast or how high it plays. The audio file on disk
/// is never touched, so clearing the edit restores the original exactly.
/// </summary>
public sealed class ClipEdit
{
    /// <summary>Where playback starts, in seconds.</summary>
    public double StartSeconds { get; set; }

    /// <summary>Where playback stops, in seconds. Zero means "to the end".</summary>
    public double EndSeconds { get; set; }

    /// <summary>Extra gain for this sound only, on top of the levelling and boost.</summary>
    public double GainDb { get; set; }

    public double FadeInMilliseconds { get; set; }

    public double FadeOutMilliseconds { get; set; }

    /// <summary>
    /// Playback rate: 2 plays twice as fast and an octave up (the chipmunk toy),
    /// 0.5 plays slow and deep (the slowed-reverb toy). 1 is untouched.
    /// </summary>
    public double Speed { get; set; } = 1;

    /// <summary>Pitch shift in semitones, without changing how long the clip lasts.</summary>
    public double PitchSemitones { get; set; }

    /// <summary>The rate playback actually uses; a missing value from an old config reads as normal.</summary>
    public double EffectiveSpeed => Math.Clamp(Speed is > 0.05 and < 20 ? Speed : 1.0, 0.5, 2.0);

    /// <summary>The pitch shift playback actually uses, capped at an octave either way.</summary>
    public double EffectivePitch => Math.Clamp(PitchSemitones, -12, 12);

    /// <summary>True when the edit changes nothing, so it need not be stored.</summary>
    public bool IsEmpty =>
        StartSeconds <= 0.001 &&
        EndSeconds <= 0.001 &&
        Math.Abs(GainDb) < 0.05 &&
        FadeInMilliseconds <= 0.5 &&
        FadeOutMilliseconds <= 0.5 &&
        Math.Abs(EffectiveSpeed - 1.0) < 0.005 &&
        Math.Abs(EffectivePitch) < 0.5;

    public ClipEdit Copy() => new()
    {
        StartSeconds = StartSeconds,
        EndSeconds = EndSeconds,
        GainDb = GainDb,
        FadeInMilliseconds = FadeInMilliseconds,
        FadeOutMilliseconds = FadeOutMilliseconds,
        Speed = Speed,
        PitchSemitones = PitchSemitones,
    };

    /// <summary>Short badge for the card, e.g. "1.2–4.5s +3.0dB fade 1.50x".</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (StartSeconds > 0.001 || EndSeconds > 0.001)
            {
                var start = StartSeconds.ToString("0.0#", CultureInfo.InvariantCulture);
                var end = EndSeconds > 0.001
                    ? EndSeconds.ToString("0.0#", CultureInfo.InvariantCulture)
                    : "end";
                parts.Add($"{start}–{end}s");
            }

            if (Math.Abs(GainDb) >= 0.05)
            {
                parts.Add(GainDb.ToString("+0.0;-0.0", CultureInfo.InvariantCulture) + "dB");
            }

            if (FadeInMilliseconds > 0.5 || FadeOutMilliseconds > 0.5)
            {
                parts.Add("fade");
            }

            var speed = EffectiveSpeed;
            if (Math.Abs(speed - 1.0) >= 0.005)
            {
                parts.Add(speed.ToString("0.00", CultureInfo.InvariantCulture) + "x");
            }

            var pitch = EffectivePitch;
            if (Math.Abs(pitch) >= 0.5)
            {
                parts.Add(pitch.ToString("+0;-0", CultureInfo.InvariantCulture) + "st");
            }

            return string.Join(" ", parts);
        }
    }
}
