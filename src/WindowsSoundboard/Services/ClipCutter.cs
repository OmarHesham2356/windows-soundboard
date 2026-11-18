using NAudio.Wave;

namespace WindowsSoundboard.Services;

/// <summary>
/// Exports a copy of a sound with a range removed. This is the one edit that cannot
/// stay in the config: the result is a new file next to the original, and the
/// original is never modified. Trims, fades and gain keep working on top.
/// </summary>
public static class ClipCutter
{
    /// <summary>
    /// Writes <paramref name="keepFrom"/>..<paramref name="keepTo"/> of a file, minus
    /// <paramref name="cutFrom"/>..<paramref name="cutTo"/>, as a new 16-bit WAV in the
    /// same folder. Returns the new path and how long it plays.
    /// </summary>
    public static (string Path, double Seconds) Cut(
        string sourcePath,
        double keepFrom,
        double keepTo,
        double cutFrom,
        double cutTo)
    {
        using var reader = new AudioFileReader(sourcePath);
        var total = reader.TotalTime.TotalSeconds;

        var from = Math.Clamp(keepFrom, 0, total);
        var to = Math.Clamp(keepTo > 0 ? keepTo : total, 0, total);
        var cutStart = Math.Clamp(cutFrom, from, to);
        var cutEnd = Math.Clamp(cutTo, cutStart, to);

        if (to - from < 0.05)
        {
            throw new InvalidOperationException("nothing left to keep");
        }

        var folder = Path.GetDirectoryName(sourcePath)
                     ?? throw new InvalidOperationException("the file has no folder");
        var target = UniquePath(folder, Path.GetFileNameWithoutExtension(sourcePath));
        var temp = target + ".part";

        try
        {
            reader.CurrentTime = TimeSpan.FromSeconds(from);
            var provider = new CutProvider(reader, from, to, cutStart, cutEnd);
            WaveFileWriter.CreateWaveFile16(temp, provider);
            File.Move(temp, target);
            return (target, provider.ProducedSeconds);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static string UniquePath(string folder, string name)
    {
        var candidate = Path.Combine(folder, $"{name} (cut).wav");
        var index = 2;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(folder, $"{name} (cut {index}).wav");
            index++;
        }

        return candidate;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A leftover .part file is harmless; the caller is already reporting a failure.
        }
    }

    /// <summary>
    /// Reads one contiguous span of the file, jumping over the cut. Frames are counted
    /// from the start of the span so the output stops exactly where the span ends.
    /// </summary>
    private sealed class CutProvider : ISampleProvider
    {
        private readonly AudioFileReader _reader;
        private readonly double _baseSeconds;
        private readonly long _cutFromFrame;
        private readonly long _cutToFrame;
        private readonly long _limitFrames;
        private long _position;
        private long _emitted;
        private bool _skipped;

        public CutProvider(AudioFileReader reader, double keepFrom, double keepTo, double cutFrom, double cutTo)
        {
            _reader = reader;
            WaveFormat = reader.WaveFormat;
            _baseSeconds = keepFrom;

            var rate = (double)WaveFormat.SampleRate;
            var from = (long)Math.Round(keepFrom * rate);
            _cutFromFrame = (long)Math.Round(cutFrom * rate) - from;
            _cutToFrame = (long)Math.Round(cutTo * rate) - from;
            _limitFrames = (long)Math.Round(keepTo * rate) - from - (_cutToFrame - _cutFromFrame);
        }

        public WaveFormat WaveFormat { get; }

        /// <summary>How long the written copy actually plays.</summary>
        public double ProducedSeconds => _emitted / (double)WaveFormat.SampleRate;

        public int Read(float[] buffer, int offset, int count)
        {
            var channels = WaveFormat.Channels;
            var frames = count / channels;
            if (frames <= 0 || _emitted >= _limitFrames)
            {
                return 0;
            }

            // Reaching the cut start jumps the reader past it; the next read carries on
            // from there, so no audio from the cut itself is ever written.
            if (!_skipped && _position >= _cutFromFrame)
            {
                _reader.CurrentTime = TimeSpan.FromSeconds(_baseSeconds + (_cutToFrame / (double)WaveFormat.SampleRate));
                _position = _cutToFrame;
                _skipped = true;
            }

            var allowed = Math.Min(frames, _limitFrames - _emitted);
            if (!_skipped)
            {
                allowed = Math.Min(allowed, _cutFromFrame - _position);
            }

            if (allowed <= 0)
            {
                return 0;
            }

            var read = _reader.Read(buffer, offset, (int)(allowed * channels));
            if (read <= 0)
            {
                return 0;
            }

            _position += read / channels;
            _emitted += read / channels;
            return read;
        }
    }
}
