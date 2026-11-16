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

    private sealed record PausedSound(string Path, double PositionSeconds, double GainDb);

    private readonly object _gate = new();
    private readonly List<PlaybackItem> _playing = [];
    private readonly List<PausedSound> _paused = [];
    private MMDevice? _device;
    private WasapiOut? _output;
    private MixingSampleProvider? _mixer;
    private VolumeSampleProvider? _volume;
    private ISampleProvider? _micInput;
    private float _gain = 0.9f;
    private double _boostDb;

    /// <summary>Format the mixer produces, so capture chains can match it.</summary>
    public WaveFormat? MixerFormat => _mixer?.WaveFormat;

    /// <summary>True while sounds are held at their position by Play / Pause.</summary>
    public bool IsPaused
    {
        get
        {
            lock (_gate)
            {
                return _paused.Count > 0;
            }
        }
    }

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

    /// <summary>
    /// Raised per file: true when a copy starts, false when the last copy of that
    /// file has finished (overlapping copies keep it true). Fires from the audio
    /// thread for endings, so listeners must marshal to the UI thread.
    /// </summary>
    public event Action<string, bool>? SoundPlaybackChanged;

    /// <summary>
    /// Raised when a file reached its natural end (not when it was stopped, paused
    /// or replaced). Loop and Continue play modes listen to this.
    /// </summary>
    public event Action<string>? SoundFinished;

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

    /// <summary>Extra gain in dB applied to sounds (never to the microphone).</summary>
    public void SetBoostDb(double boostDb)
        => _boostDb = Math.Clamp(boostDb, 0.0, 30.0);

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

    public void Play(string path, TriggerMode mode, double gainDb = 0, bool loop = false)
        => PlayCore(path, mode, gainDb, loop, startAtSeconds: 0);

    /// <summary>Starts a held sound, replacing any copy already held for that file.</summary>
    public void HoldStart(string path, double gainDb = 0, bool loop = false)
        => PlayCore(path, TriggerMode.Stop, gainDb, loop, startAtSeconds: 0);

    private void PlayCore(string path, TriggerMode mode, double gainDb, bool loop, double startAtSeconds)
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

                if (startAtSeconds > 0.05 && startAtSeconds < reader.TotalTime.TotalSeconds)
                {
                    reader.CurrentTime = TimeSpan.FromSeconds(startAtSeconds);
                }

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

                var totalGainDb = gainDb + _boostDb;
                if (Math.Abs(totalGainDb) > 0.05)
                {
                    source = new VolumeSampleProvider(source)
                    {
                        Volume = (float)Math.Pow(10, totalGainDb / 20.0),
                    };
                }

                var item = new PlaybackItem(path, reader, source, FadeMilliseconds, loop, gainDb);
                _playing.Add(item);
                _mixer.AddMixerInput(item);
                Log.Info($"play {Path.GetFileName(path)} ({mode}, {totalGainDb:+0.0;-0.0;0.0} dB" +
                         $"{(loop ? ", loop" : string.Empty)}{(startAtSeconds > 0.05 ? $", from {startAtSeconds:0.0}s" : string.Empty)})");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"playing {Path.GetFileName(path)} failed", ex);
            return;
        }

        AnyPlayingChanged?.Invoke(true);
        SoundPlaybackChanged?.Invoke(path, true);
    }

    /// <summary>Fades out the held copy of a file (play &amp; hold release).</summary>
    public void HoldStop(string path)
    {
        lock (_gate)
        {
            StopPathLocked(path);
        }

        AnyPlayingChanged?.Invoke(true);
    }

    /// <summary>Stops everything and forgets any paused position.</summary>
    public void StopAll()
    {
        bool any;
        lock (_gate)
        {
            _paused.Clear();
            any = _playing.Count > 0;
            foreach (var item in _playing)
            {
                item.RequestStop();
            }
        }

        if (any)
        {
            AnyPlayingChanged?.Invoke(false);
        }
    }

    /// <summary>
    /// Holds the playing sounds where they are, remembering their positions so
    /// <see cref="ResumeSounds"/> can carry on. The microphone keeps running.
    /// </summary>
    public void PauseSounds()
    {
        lock (_gate)
        {
            if (_playing.Count == 0)
            {
                return;
            }

            _paused.Clear();
            foreach (var item in _playing)
            {
                _paused.Add(new PausedSound(item.Path, item.PositionSeconds, item.GainDb));
            }

            foreach (var item in _playing)
            {
                item.RequestStop();
            }
        }

        Log.Info("paused");
    }

    /// <summary>Restarts every sound that <see cref="PauseSounds"/> held.</summary>
    public void ResumeSounds()
    {
        List<PausedSound> resume;
        lock (_gate)
        {
            resume = [.. _paused];
            _paused.Clear();
        }

        if (resume.Count == 0)
        {
            return;
        }

        foreach (var sound in resume)
        {
            PlayCore(sound.Path, TriggerMode.Stop, sound.GainDb, loop: false, sound.PositionSeconds);
        }

        Log.Info("resumed");
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

        bool stillPlayingSameFile;
        lock (_gate)
        {
            _playing.Remove(item);
            stillPlayingSameFile = _playing.Any(other =>
                string.Equals(other.Path, item.Path, StringComparison.OrdinalIgnoreCase));
        }

        var naturalEnd = item.NaturalEnd;
        item.Dispose();
        AnyPlayingChanged?.Invoke(_playing.Count > 0);

        if (!stillPlayingSameFile)
        {
            Log.Info($"finished {Path.GetFileName(item.Path)}");
            SoundPlaybackChanged?.Invoke(item.Path, false);

            if (naturalEnd)
            {
                SoundFinished?.Invoke(item.Path);
            }
        }
    }

    private void CloseOutputLocked()
    {
        foreach (var item in _playing)
        {
            item.RequestStop();
            item.Dispose();
        }

        _playing.Clear();
        _paused.Clear();

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
/// key or restarting a sound does not click. A looping copy seeks back to the start
/// instead of ending.
/// </summary>
internal sealed class PlaybackItem : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly AudioFileReader? _reader;
    private readonly bool _loop;
    private readonly float _fadeStep;
    private volatile bool _stopping;
    private bool _finished;
    private float _gain = 1f;

    public PlaybackItem(
        string path,
        AudioFileReader? reader,
        ISampleProvider source,
        int fadeMilliseconds,
        bool loop,
        double gainDb)
    {
        Path = path;
        _reader = reader;
        _source = source;
        _loop = loop;
        GainDb = gainDb;
        WaveFormat = source.WaveFormat;
        var fadeSamples = Math.Max(1f, WaveFormat.SampleRate * fadeMilliseconds / 1000f);
        _fadeStep = 1f / fadeSamples;
    }

    public string Path { get; }

    public WaveFormat WaveFormat { get; }

    /// <summary>Gain this copy was started with, so pause can resume it the same way.</summary>
    public double GainDb { get; }

    /// <summary>Where the file has got to, in seconds.</summary>
    public double PositionSeconds
    {
        get
        {
            try
            {
                return _reader?.CurrentTime.TotalSeconds ?? 0;
            }
            catch
            {
                return 0;
            }
        }
    }

    /// <summary>True when the file ran out on its own rather than being stopped.</summary>
    public bool NaturalEnd { get; private set; }

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
            if (_loop && !_stopping && SeekToStart())
            {
                read = _source.Read(buffer, offset, count);
            }

            if (read == 0)
            {
                _finished = true;
                NaturalEnd = !_stopping;
                return 0;
            }
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

    private bool SeekToStart()
    {
        try
        {
            if (_reader is null)
            {
                return false;
            }

            _reader.CurrentTime = TimeSpan.Zero;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => (_source as IDisposable)?.Dispose();
}
