using System.Globalization;

namespace WindowsSoundboard.Models;

/// <summary>
/// A non-destructive edit of one sound: where playback starts and ends, how it
/// fades, and its own gain. The audio file on disk is never touched, so clearing
/// the edit restores the original exactly.
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

    /// <summary>True when the edit changes nothing, so it need not be stored.</summary>
    public bool IsEmpty =>
        StartSeconds <= 0.001 &&
        EndSeconds <= 0.001 &&
        Math.Abs(GainDb) < 0.05 &&
        FadeInMilliseconds <= 0.5 &&
        FadeOutMilliseconds <= 0.5;

    public ClipEdit Copy() => new()
    {
        StartSeconds = StartSeconds,
        EndSeconds = EndSeconds,
        GainDb = GainDb,
        FadeInMilliseconds = FadeInMilliseconds,
        FadeOutMilliseconds = FadeOutMilliseconds,
    };

    /// <summary>Short badge for the card, e.g. "1.2–4.5s +3.0dB".</summary>
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

            return string.Join(" ", parts);
        }
    }
}
