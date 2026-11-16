using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using WindowsSoundboard.Models;

namespace WindowsSoundboard.Services;

public sealed record AudioDevice(string Id, string Name, bool IsVirtualCable)
{
    public string DisplayName => IsVirtualCable ? $"{Name}  (virtual cable)" : Name;

    public override string ToString() => DisplayName;
}

/// <summary>
/// Plays sound files into one WASAPI output endpoint through a single mixer, so
/// several sounds overlap and each can be stopped on its own (what Play &amp; Hold needs).
/// The output stream stays open for the app's lifetime: no per-sound device churn.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private const int FadeMilliseconds = 12;

    private readonly object _gate = new();
    private readonly List<PlaybackItem> _playing = [];
    private MMDevice? _device;
    private WasapiOut? _output;
    private MixingSampleProvider? _mixer;
    private VolumeSampleProvider? _volume;
    private ISampleProvider? _micInput;
    private float _gain = 0.9f;

    /// <summary>Format the mixer produces, so capture chains can match it.</summary>
    public WaveFormat? MixerFormat => _mixer?.WaveFormat;

    /// <summary>Adds (or clears, with null) the microphone chain mixed into the output.</summary>
    public void SetMicInput(ISampleProvider? input)
    {
        lock (_gate)
        {
            if (_micInput is not null && _mixer is not null)
            {
                _mixer.RemoveMixerInput(_micInput);
            }

            _micInput = input;

            if (input is not null && _mixer is not null)
            {
                _mixer.AddMixerInput(input);
            }
        }
    }

    /// <summary>Raised with true when sound starts and false when the last sound ends.</summary>
    public event Action<bool>? AnyPlayingChanged;

    public string CurrentDeviceId { get; private set; } = string.Empty;

    public IReadOnlyList<AudioDevice> ListOutputs()
    {
        var devices = new List<AudioDevice>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                devices.Add(new AudioDevice(
                    device.ID, device.FriendlyName, VirtualCable.LooksLikeCable(device.FriendlyName)));
                device.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Error("listing output devices failed", ex);
        }

        return devices;
    }

    /// <summary>Opens (or reopens) the output stream on the given endpoint.</summary>
    public void SetOutput(string deviceId)
    {
        lock (_gate)
        {
            try
            {
                CloseOutputLocked();
                using var enumerator = new MMDeviceEnumerator();
                _device = ResolveDevice(enumerator, deviceId);
                if (_device is null)
                {
                    Log.Warn("no render endpoint available");
                    return;
                }

                var mix = _device.AudioClient.MixFormat;
                var mixerFormat = WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, mix.Channels);
                _mixer = new MixingSampleProvider(mixerFormat) { ReadFully = true };
                _mixer.MixerInputEnded += OnMixerInputEnded;
                _volume = new VolumeSampleProvider(_mixer) { Volume = IsMuted ? 0f : _gain };

                var provider = mix.Encoding == WaveFormatEncoding.IeeeFloat
                    ? _volume.ToWaveProvider()
                    : _volume.ToWaveProvider16();

                _output = new WasapiOut(_device, AudioClientShareMode.Shared, true, 40);
                _output.Init(provider);
                _output.Play();

                // A reopened stream has a new mixer: put the microphone back on it.
                _micInput = null;

                CurrentDeviceId = _device.ID;
                Log.Info($"output opened: '{_device.FriendlyName}' {mix.SampleRate} Hz, {mix.Channels} ch, {mix.Encoding}");
            }
            catch (Exception ex)
            {
                Log.Error($"opening output '{deviceId}' failed", ex);
                CloseOutputLocked();
            }
        }
    }

    public bool IsMuted { get; private set; }

    public void SetVolume(double volume)
    {
        _gain = (float)Math.Clamp(volume, 0.0, 1.5);
        lock (_gate)
        {
            if (_volume is not null)
            {
                _volume.Volume = IsMuted ? 0f : _gain;
            }
        }
    }

    /// <summary>Silences the cable without losing the volume setting.</summary>
    public void SetMuted(bool muted)
    {
        IsMuted = muted;
        lock (_gate)
        {
            if (_volume is not null)
            {
                _volume.Volume = muted ? 0f : _gain;
            }
        }
    }

    public void Play(string path, TriggerMode mode, double gainDb = 0)
    {
        try
        {
            lock (_gate)
            {
                if (_mixer is null || _output is null)
                {
                    Log.Warn("play ignored: no output stream");
                    return;
                }

                if (mode != TriggerMode.Overlap)
                {
                    StopPathLocked(path);
                }

                var reader = new AudioFileReader(path);
                ISampleProvider source = reader;

                if (source.WaveFormat.Channels == 1 && _mixer.WaveFormat.Channels == 2)
                {
                    source = new MonoToStereoSampleProvider(source);
                }

                if (source.WaveFormat.SampleRate != _mixer.WaveFormat.SampleRate)
                {
                    source = new WdlResamplingSampleProvider(source, _mixer.WaveFormat.SampleRate);
                }

                if (source.WaveFormat.Channels != _mixer.WaveFormat.Channels)
                {
                    Log.Warn($"{Path.GetFileName(path)}: {source.WaveFormat.Channels} channels do not fit a " +
                             $"{_mixer.WaveFormat.Channels}-channel output");
                    reader.Dispose();
                    return;
                }

                if (Math.Abs(gainDb) > 0.05)
                {
                    source = new VolumeSampleProvider(source) { Volume = (float)Math.Pow(10, gainDb / 20.0) };
                }

                var item = new PlaybackItem(path, source, FadeMilliseconds);
                _playing.Add(item);
                _mixer.AddMixerInput(item);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"playing {Path.GetFileName(path)} failed", ex);
        }

        AnyPlayingChanged?.Invoke(true);
    }

    /// <summary>Starts a held sound, replacing any copy already held for that file.</summary>
    public void HoldStart(string path, double gainDb = 0) => Play(path, TriggerMode.Stop, gainDb);

    /// <summary>Fades out the held copy of a file (Play &amp; Hold release).</summary>
    public void HoldStop(string path)
    {
        lock (_gate)
        {
            StopPathLocked(path);
        }

        AnyPlayingChanged?.Invoke(true);
    }

    public void StopAll()
    {
        lock (_gate)
        {
            foreach (var item in _playing)
            {
                item.RequestStop();
            }
        }
    }

    private void StopPathLocked(string path)
    {
        foreach (var item in _playing)
        {
            if (string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                item.RequestStop();
            }
        }
    }

    private static MMDevice? ResolveDevice(MMDeviceEnumerator enumerator, string deviceId)
    {
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            try
            {
                return enumerator.GetDevice(deviceId);
            }
            catch (Exception ex)
            {
                Log.Warn($"saved output device is gone ({ex.GetType().Name}); falling back");
            }
        }

        // Prefer the playback side of a virtual cable: that is what other apps listen to.
        foreach (var candidate in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            if (VirtualCable.LooksLikeCable(candidate.FriendlyName))
            {
                return candidate;
            }

            candidate.Dispose();
        }

        return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    private void OnMixerInputEnded(object? sender, SampleProviderEventArgs args)
    {
        if (args.SampleProvider is not PlaybackItem item)
        {
            return;
        }

        lock (_gate)
        {
            _playing.Remove(item);
        }

        item.Dispose();
        AnyPlayingChanged?.Invoke(_playing.Count > 0);
    }

    private void CloseOutputLocked()
    {
        foreach (var item in _playing)
        {
            item.RequestStop();
            item.Dispose();
        }

        _playing.Clear();

        if (_mixer is not null)
        {
            _mixer.MixerInputEnded -= OnMixerInputEnded;
        }

        _output?.Stop();
        _output?.Dispose();
        _output = null;
        _mixer = null;
        _volume = null;

        _device?.Dispose();
        _device = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            CloseOutputLocked();
        }
    }
}

/// <summary>
/// One playing sound. Stopping fades over a few milliseconds, so releasing a held
/// key or restarting a sound does not click.
/// </summary>
internal sealed class PlaybackItem : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly float _fadeStep;
    private volatile bool _stopping;
    private bool _finished;
    private float _gain = 1f;

    public PlaybackItem(string path, ISampleProvider source, int fadeMilliseconds)
    {
        Path = path;
        _source = source;
        WaveFormat = source.WaveFormat;
        var fadeSamples = Math.Max(1f, WaveFormat.SampleRate * fadeMilliseconds / 1000f);
        _fadeStep = 1f / fadeSamples;
    }

    public string Path { get; }

    public WaveFormat WaveFormat { get; }

    public void RequestStop() => _stopping = true;

    public int Read(float[] buffer, int offset, int count)
    {
        if (_finished)
        {
            return 0;
        }

        var read = _source.Read(buffer, offset, count);
        if (read == 0)
        {
            _finished = true;
            return 0;
        }

        if (!_stopping)
        {
            return read;
        }

        var channels = WaveFormat.Channels;
        var frames = read / channels;
        for (var frame = 0; frame < frames; frame++)
        {
            _gain -= _fadeStep;
            var start = offset + (frame * channels);
            if (_gain <= 0f)
            {
                Array.Clear(buffer, start, read - (frame * channels));
                _finished = true;
                return frame * channels;
            }

            for (var channel = 0; channel < channels; channel++)
            {
                buffer[start + channel] *= _gain;
            }
        }

        return read;
    }

    public void Dispose() => (_source as IDisposable)?.Dispose();
}
