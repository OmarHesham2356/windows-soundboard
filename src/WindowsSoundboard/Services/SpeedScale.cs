using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace WindowsSoundboard.Services;

/// <summary>
/// Plays a stream faster or slower with the pitch following it — the tape-speed
/// effect behind both the chipmunk voice and the slowed-down reverb toy.
/// <para>
/// The trick is to resample to rate × speed and then claim the original rate.
/// The mixer keeps consuming the stream at its normal speed, so the extra samples
/// are eaten faster: the audio is squeezed into a shorter time and its pitch rises
/// by the same factor. No custom interpolation to get wrong.
/// </para>
/// </summary>
internal sealed class SpeedScale : ISampleProvider
{
    private readonly ISampleProvider _inner;

    private SpeedScale(ISampleProvider inner, WaveFormat format)
    {
        _inner = inner;
        WaveFormat = format;
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    /// <summary>Wraps a stream so it plays <paramref name="speed"/> times faster.</summary>
    public static ISampleProvider Apply(ISampleProvider source, double speed)
    {
        var rate = Math.Clamp(speed, 0.25, 4.0);
        if (Math.Abs(rate - 1.0) < 0.005)
        {
            return source;
        }

        var sampleRate = source.WaveFormat.SampleRate;
        var channels = source.WaveFormat.Channels;

        // Resample to the rate the clip would have if it were played at `rate`, then
        // tell everything downstream it is still the original rate.
        var scaled = new WdlResamplingSampleProvider(source, (int)Math.Round(sampleRate * rate));
        return new SpeedScale(scaled, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels));
    }
}
