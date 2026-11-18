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

    private sealed record PausedSound(string Path, double PositionSeconds, double GainDb, bool Loop, ClipEdit? Clip);

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
    private readonly string _label;

    /// <summary>Names this engine's lines in the log, so a mirrored monitor stream is distinguishable.</summary>
    public AudioEngine(string label = "output") => _label = label;

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

    /// <summary>Friendly name of the endpoint currently open, for status text.</summary>
    public string CurrentDeviceName { get; private set; } = string.Empty;

    /// <summary>
    /// The Windows default playback endpoint. Discord's screen-share audio is a
    /// loopback of this device, so anything playing here is heard in a share
    /// whatever the microphone mute says.
    /// </summary>
    public string DefaultOutputId { get; private set; } = string.Empty;

    public string DefaultOutputName { get; private set; } = string.Empty;

    public void RefreshDefaultOutput()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            DefaultOutputId = device.ID;
            DefaultOutputName = device.FriendlyName;
        }
        catch (Exception ex)
        {
            Log.Warn($"reading the default playback device failed: {ex.GetType().Name}");
        }
    }

    /// <summary>True when the board's output is the device screen shares capture.</summary>
    public bool OutputIsDefault(string deviceId)
        => !string.IsNullOrEmpty(DefaultOutputId) &&
           string.Equals(DefaultOutputId, deviceId, StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<AudioDevice> ListOutputs()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
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

        Log.Info($"timing: listed {devices.Count} outputs in {clock.ElapsedMilliseconds} ms");
        return devices;
    }

    /// <summary>Opens (or reopens) the output stream on the given endpoint.</summary>
    public void SetOutput(string deviceId)
    {
        lock (_gate)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
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

                var resolvedAt = clock.ElapsedMilliseconds;
                var mix = _device.AudioClient.MixFormat;
                var mixFormatAt = clock.ElapsedMilliseconds;
                var mixerFormat = WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, mix.Channels);
                _mixer = new MixingSampleProvider(mixerFormat) { ReadFully = true };
                _mixer.MixerInputEnded += OnMixerInputEnded;
                _volume = new VolumeSampleProvider(_mixer) { Volume = IsMuted ? 0f : _gain };

                var provider = mix.Encoding == WaveFormatEncoding.IeeeFloat
                    ? _volume.ToWaveProvider()
                    : _volume.ToWaveProvider16();

                _output = new WasapiOut(_device, AudioClientShareMode.Shared, true, 40);
                _output.Init(provider);
                var initAt = clock.ElapsedMilliseconds;
                _output.Play();
                var playAt = clock.ElapsedMilliseconds;

                // A reopened stream has a new mixer: put the microphone back on it.
                _micInput = null;

                CurrentDeviceId = _device.ID;
                CurrentDeviceName = _device.FriendlyName;
                RefreshDefaultOutput();

                Log.Info($"{_label}: output opened: '{_device.FriendlyName}' {mix.SampleRate} Hz, {mix.Channels} ch, " +
                         $"{mix.Encoding}; default playback is '{DefaultOutputName}'");
                Log.Info($"timing: {_label} resolve {resolvedAt} ms, mix format {mixFormatAt - resolvedAt} ms, " +
                         $"init {initAt - mixFormatAt} ms, play {playAt - initAt} ms, total {clock.ElapsedMilliseconds} ms");
            }
            catch (Exception ex)
            {
                Log.Error($"opening output '{deviceId}' failed", ex);
                CloseOutputLocked();
            }
        }
    }

    public bool IsMuted { get; private set; }

    /// <summary>Master gain the output stream is playing at right now.</summary>
    public double Volume => _gain;

    /// <summary>Gain in dB added on top of the levelling for sounds.</summary>
    public double BoostDb => _boostDb;

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

    public void Play(string path, TriggerMode mode, double gainDb = 0, bool loop = false, ClipEdit? clip = null)
        => PlayCore(path, mode, gainDb, loop, startAtSeconds: 0, clip);

    /// <summary>Starts a held sound, replacing any copy already held for that file.</summary>
    public void HoldStart(string path, double gainDb = 0, bool loop = false, ClipEdit? clip = null)
        => PlayCore(path, TriggerMode.Stop, gainDb, loop, startAtSeconds: 0, clip);

    private void PlayCore(string path, TriggerMode mode, double gainDb, bool loop, double startAtSeconds, ClipEdit? clip)
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

                // A resumed sound keeps its position; a trimmed one starts at the trim.
                var clipStart = clip?.StartSeconds ?? 0;
                var start = Math.Max(startAtSeconds, clipStart);
                if (start > 0.05 && start < reader.TotalTime.TotalSeconds)
                {
                    reader.CurrentTime = TimeSpan.FromSeconds(start);
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

                // Speed comes before the gain and the trims: the trim window below is
                // measured in what actually comes out of the mixer, so a clip played at
                // double rate reaches its trim end in half the source time.
                var speed = clip?.EffectiveSpeed ?? 1.0;
                var pitch = clip?.EffectivePitch ?? 0;
                if (Math.Abs(speed - 1.0) > 0.005)
                {
                    source = SpeedScale.Apply(source, speed);
                }

                if (Math.Abs(pitch) >= 0.05)
                {
                    source = new SmbPitchShiftingSampleProvider(source)
                    {
                        PitchFactor = (float)Math.Pow(2, pitch / 12.0),
                    };
                }

                var totalGainDb = gainDb + _boostDb + (clip?.GainDb ?? 0);
                if (Math.Abs(totalGainDb) > 0.05)
                {
                    source = new VolumeSampleProvider(source)
                    {
                        Volume = (float)Math.Pow(10, totalGainDb / 20.0),
                    };
                }

                // The window is measured from the clip's own start, not from a resumed
                // position, so a wrap and a resume agree on where the trim ends.
                var sampleRate = _mixer.WaveFormat.SampleRate;
                var limitFrames = 0L;
                if (clip is not null && clip.EndSeconds > clipStart + 0.02)
                {
                    limitFrames = (long)Math.Round((clip.EndSeconds - clipStart) * sampleRate / speed);
                }

                var startFrame = (long)Math.Round((start - clipStart) * sampleRate / speed);

                var fadeIn = clip is null ? 0 : (int)Math.Clamp(clip.FadeInMilliseconds, 0, 5000);
                var fadeOut = clip is null ? 0 : (int)Math.Clamp(clip.FadeOutMilliseconds, 0, 5000);

                var clipNote = clip is null
                    ? string.Empty
                    : $", clip {clip.StartSeconds:0.0#}s-{(clip.EndSeconds > 0.02 ? clip.EndSeconds.ToString("0.0#") : "end")}s " +
                      $"fade {fadeIn}/{fadeOut}ms" +
                      (Math.Abs(speed - 1.0) > 0.005 ? $", speed {speed:0.00}x" : string.Empty) +
                      (Math.Abs(pitch) >= 0.05 ? $", pitch {pitch:+0;-0}st" : string.Empty);

                var item = new PlaybackItem(
                    path, reader, source, FadeMilliseconds, loop, gainDb, clip,
                    TimeSpan.FromSeconds(clipStart), startFrame, limitFrames, fadeIn, fadeOut);
                _playing.Add(item);
                _mixer.AddMixerInput(item);

                Log.Info($"{_label}: play {Path.GetFileName(path)} ({mode}, {totalGainDb:+0.0;-0.0;0.0} dB" +
                         $"{(loop ? ", loop" : string.Empty)}{(startAtSeconds > 0.05 ? $", from {startAtSeconds:0.0}s" : string.Empty)}" +
                         $"{clipNote})");
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
                _paused.Add(new PausedSound(item.Path, item.PositionSeconds, item.GainDb, item.Loop, item.Clip));
            }

            foreach (var item in _playing)
            {
                item.RequestStop();
            }
        }

        Log.Info($"{_label}: paused");
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
            PlayCore(sound.Path, TriggerMode.Stop, sound.GainDb, sound.Loop, sound.PositionSeconds, sound.Clip);
        }

        Log.Info($"{_label}: resumed");
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
            Log.Info($"{_label}: finished {Path.GetFileName(item.Path)}");
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
    private readonly ClipEdit? _clip;
    private readonly TimeSpan _startTime;
    private readonly long _limitFrames;
    private readonly long _fadeInFrames;
    private readonly long _fadeOutFrames;
    private volatile bool _stopping;
    private bool _finished;
    private bool _wrapPending;
    private float _gain = 1f;
    private long _frames;

    public PlaybackItem(
        string path,
        AudioFileReader? reader,
        ISampleProvider source,
        int fadeMilliseconds,
        bool loop,
        double gainDb,
        ClipEdit? clip,
        TimeSpan startTime,
        long startFrame,
        long limitFrames,
        int fadeInMilliseconds,
        int fadeOutMilliseconds)
    {
        Path = path;
        _reader = reader;
        _source = source;
        _loop = loop;
        _clip = clip;
        _startTime = startTime;
        _frames = Math.Max(0, startFrame);
        _limitFrames = limitFrames;
        GainDb = gainDb;
        WaveFormat = source.WaveFormat;

        var fadeSamples = Math.Max(1f, WaveFormat.SampleRate * fadeMilliseconds / 1000f);
        _fadeStep = 1f / fadeSamples;
        _fadeInFrames = (long)(WaveFormat.SampleRate * Math.Max(0, fadeInMilliseconds) / 1000.0);
        _fadeOutFrames = (long)(WaveFormat.SampleRate * Math.Max(0, fadeOutMilliseconds) / 1000.0);
    }

    public string Path { get; }

    public WaveFormat WaveFormat { get; }

    /// <summary>Gain this copy was started with, so pause can resume it the same way.</summary>
    public double GainDb { get; }

    /// <summary>True when this copy wraps instead of ending.</summary>
    public bool Loop => _loop;

    /// <summary>The trim/gain this copy is playing with, if any.</summary>
    public ClipEdit? Clip => _clip;

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

    /// <summary>How many times the file has wrapped back to its start.</summary>
    public int Loops { get; private set; }

    public void RequestStop() => _stopping = true;

    public int Read(float[] buffer, int offset, int count)
    {
        var produced = 0;
        var guard = 0;

        while (produced < count && guard++ < 512)
        {
            if (_finished)
            {
                break;
            }

            // A trim end reached on a looping clip wraps back to the trim start and
            // keeps filling this same buffer, so the mixer never sees a short read
            // and never treats the loop as the end of the stream.
            if (_wrapPending && !Wrap())
            {
                break;
            }

            // A chunk can legitimately be empty when it starts exactly on the trim
            // boundary; that is a wrap waiting to happen, not the end of the stream.
            var read = ReadChunk(buffer, offset + produced, count - produced);
            if (read == 0 && !_wrapPending)
            {
                break;
            }

            produced += read;

            if (!_wrapPending)
            {
                break;
            }
        }

        return produced;
    }

    /// <summary>Restarts a looping clip at its trim start; false when it cannot.</summary>
    private bool Wrap()
    {
        _wrapPending = false;
        if (!SeekToStart())
        {
            _finished = true;
            NaturalEnd = !_stopping;
            return false;
        }

        Loops++;
        if (Loops == 1)
        {
            Log.Info($"looping {System.IO.Path.GetFileName(Path)}");
        }

        _frames = 0;
        return true;
    }

    private int ReadChunk(float[] buffer, int offset, int count)
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
                Loops++;
                if (Loops == 1)
                {
                    Log.Info($"looping {System.IO.Path.GetFileName(Path)}");
                }

                read = _source.Read(buffer, offset, count);

                // A wrap restarts the trim window and the fade in.
                _frames = 0;
            }

            if (read == 0)
            {
                _finished = true;
                NaturalEnd = !_stopping;
                return 0;
            }
        }

        var channels = WaveFormat.Channels;
        var frames = read / channels;
        var written = frames;

        for (var frame = 0; frame < frames; frame++)
        {
            var position = _frames + frame;
            var envelope = 1f;

            if (_fadeInFrames > 0 && position < _fadeInFrames)
            {
                envelope = position / (float)_fadeInFrames;
            }

            if (_limitFrames > 0)
            {
                var remaining = _limitFrames - position;
                if (remaining <= 0)
                {
                    written = frame;
                    if (_loop && !_stopping)
                    {
                        // A loop should wrap back to the trim start, not stop.
                        _wrapPending = true;
                    }
                    else
                    {
                        // The trim has been reached: end here, like the file running out.
                        _finished = true;
                        NaturalEnd = !_stopping;
                    }

                    break;
                }

                if (_fadeOutFrames > 0 && remaining < _fadeOutFrames)
                {
                    envelope *= remaining / (float)_fadeOutFrames;
                }
            }

            if (_stopping)
            {
                _gain -= _fadeStep;
                if (_gain <= 0f)
                {
                    written = frame;
                    _finished = true;
                    break;
                }

                envelope *= _gain;
            }

            var start = offset + (frame * channels);
            for (var channel = 0; channel < channels; channel++)
            {
                buffer[start + channel] *= envelope;
            }
        }

        var produced = written * channels;
        if (produced < read)
        {
            Array.Clear(buffer, offset + produced, read - produced);
        }

        _frames += written;
        return produced;
    }

    private bool SeekToStart()
    {
        try
        {
            if (_reader is null)
            {
                return false;
            }

            _reader.CurrentTime = _startTime;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => (_source as IDisposable)?.Dispose();
}
