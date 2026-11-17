using NAudio.Wave;

namespace WindowsSoundboard.Services;

/// <summary>
/// Decodes a file once and reduces it to peak buckets, so a waveform can be drawn
/// without keeping the audio in memory. Done off the UI thread; a long file costs a
/// single decode pass.
/// </summary>
public static class Waveform
{
    /// <summary>
    /// Returns <paramref name="buckets"/> peaks in the range 0..1 and the file's
    /// duration. An unreadable file returns an empty array.
    /// </summary>
    public static (float[] Peaks, double Seconds) Measure(string path, int buckets = 480)
    {
        buckets = Math.Clamp(buckets, 32, 2000);

        try
        {
            using var reader = new AudioFileReader(path);
            var total = reader.TotalTime.TotalSeconds;
            var peaks = new float[buckets];
            var buffer = new float[reader.WaveFormat.SampleRate * reader.WaveFormat.Channels / 4];
            var index = 0;
            int read;

            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                var channels = reader.WaveFormat.Channels;
                for (var i = 0; i < read; i += channels)
                {
                    var peak = 0f;
                    for (var channel = 0; channel < channels && i + channel < read; channel++)
                    {
                        peak = Math.Max(peak, Math.Abs(buffer[i + channel]));
                    }

                    // Where this sample belongs, scaled across the whole file.
                    var position = total > 0 ? reader.CurrentTime.TotalSeconds / total : 0;
                    var bucket = (int)(Math.Clamp(position, 0, 0.999) * buckets);
                    if (bucket >= 0 && bucket < buckets)
                    {
                        peaks[bucket] = Math.Max(peaks[bucket], peak);
                    }
                }

                index += read;
            }

            // Nothing decoded: fall back to a flat line so the dialog still works.
            if (index == 0)
            {
                Array.Fill(peaks, 0.02f);
            }

            return (peaks, total);
        }
        catch (Exception ex)
        {
            Log.Warn($"waveform failed for {System.IO.Path.GetFileName(path)}: {ex.GetType().Name}");
            return ([], 0);
        }
    }
}
