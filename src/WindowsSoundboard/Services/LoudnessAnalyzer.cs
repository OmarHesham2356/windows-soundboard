namespace WindowsSoundboard.Services;

/// <summary>
/// Measures how loud a file actually is and works out the gain that brings it to a
/// common level, so a whisper-quiet meme and a loud one do not arrive 20 dB apart.
///
/// This is a plain RMS measurement rather than full EBU R128 gating: it needs one
/// decode pass, no lookahead state, and it lands within a decibel or two of R128 on
/// the short clips a soundboard actually holds.
/// </summary>
public static class LoudnessAnalyzer
{
    /// <summary>Level every sound is pushed towards, in dBFS RMS.</summary>
    public const double TargetRmsDb = -18.0;

    /// <summary>Never boost more than this: it would only raise the noise floor.</summary>
    public const double MaxBoostDb = 18.0;

    /// <summary>Never cut more than this, or already-quiet sounds become inaudible.</summary>
    public const double MaxCutDb = -18.0;

    private const int MaxAnalysisSeconds = 60;

    /// <summary>
    /// Returns the gain in dB to apply, or null when the file could not be read.
    /// Silence yields 0 dB rather than an enormous boost.
    /// </summary>
    public static double? MeasureGain(string path)
    {
        try
        {
            using var reader = new NAudio.Wave.AudioFileReader(path);
            var sampleRate = reader.WaveFormat.SampleRate;
            var channels = reader.WaveFormat.Channels;
            var maxSamples = (long)sampleRate * channels * MaxAnalysisSeconds;

            var buffer = new float[8192];
            double sumOfSquares = 0;
            long sampleCount = 0;

            while (sampleCount < maxSamples)
            {
                var read = reader.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }

                for (var i = 0; i < read; i++)
                {
                    var value = buffer[i];
                    sumOfSquares += value * value;
                }

                sampleCount += read;
            }

            if (sampleCount == 0)
            {
                return null;
            }

            var meanSquare = sumOfSquares / sampleCount;
            if (meanSquare < 1e-9)
            {
                // Effectively silent: amplifying it would only raise noise.
                return 0.0;
            }

            var rmsDb = 10 * Math.Log10(meanSquare);
            var gain = TargetRmsDb - rmsDb;
            return Math.Clamp(gain, MaxCutDb, MaxBoostDb);
        }
        catch (Exception ex)
        {
            Log.Warn($"loudness of {Path.GetFileName(path)} unreadable: {ex.GetType().Name}");
            return null;
        }
    }
}
