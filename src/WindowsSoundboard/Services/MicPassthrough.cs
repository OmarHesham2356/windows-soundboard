using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace WindowsSoundboard.Services;

public sealed record MicDevice(string Id, string Name, bool IsVirtualCable);

/// <summary>
/// Captures a microphone and returns it as a sample provider that can be mixed into
/// the soundboard's output, so the voice goes to the virtual cable alongside sounds.
///
/// The chain is fixed for the lifetime of a start: capture format to float, mono to
/// the output channel count, resampled only when the rates differ. <c>ReadFully</c>
/// keeps the provider alive through gaps (the mixer treats a zero-length read as the
/// end of a sound), so silence is what gets played while nobody talks.
/// </summary>
public sealed class MicPassthrough : IDisposable
{
    private readonly object _gate = new();
    private WasapiCapture? _capture;
    private MMDevice? _device;
    private BufferedWaveProvider? _buffer;
    private VolumeSampleProvider? _volume;
    private float _gain = 1f;

    /// <summary>Capture buffer length: a shorter one lowers latency at the cost of slack.</summary>
    public int BufferMilliseconds { get; set; } = 400;

    public string CurrentDeviceId { get; private set; } = string.Empty;

    public bool IsRunning => _capture is not null;

    /// <summary>Volume the microphone is mixed at right now.</summary>
    public double Gain => _gain;

    public IReadOnlyList<MicDevice> ListInputs()
    {
        var devices = new List<MicDevice>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                devices.Add(new MicDevice(device.ID, device.FriendlyName, VirtualCable.LooksLikeCable(device.FriendlyName)));
                device.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Error("listing microphones failed", ex);
        }

        // Real microphones first: capturing a cable would echo the soundboard back into itself.
        return devices.OrderBy(device => device.IsVirtualCable).ToList();
    }

    public void SetGain(double gain)
    {
        _gain = (float)Math.Clamp(gain, 0.0, 4.0);
        lock (_gate)
        {
            if (_volume is not null)
            {
                _volume.Volume = _gain;
            }
        }
    }

    /// <summary>
    /// Starts capturing and returns the provider to give the engine's mixer, or null
    /// when the microphone could not be opened.
    /// </summary>
    public ISampleProvider? Start(string deviceId, WaveFormat targetFormat)
    {
        Stop();

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            MMDevice? device = null;
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                try
                {
                    device = enumerator.GetDevice(deviceId);
                }
                catch (Exception ex)
                {
                    Log.Warn($"microphone '{deviceId}' is gone ({ex.GetType().Name}); using the default");
                }
            }

            device ??= enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
                       ?? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);

            if (device is null)
            {
                Log.Warn("no capture endpoint available");
                return null;
            }

            var capture = new WasapiCapture(device);
            var inputFormat = capture.WaveFormat;

            var buffer = new BufferedWaveProvider(inputFormat)
            {
                BufferDuration = TimeSpan.FromMilliseconds(Math.Clamp(BufferMilliseconds, 60, 1000)),
                DiscardOnBufferOverflow = true,
                ReadFully = true,
            };

            capture.DataAvailable += (_, args) =>
            {
                try
                {
                    buffer.AddSamples(args.Buffer, 0, args.BytesRecorded);
                }
                catch (Exception ex)
                {
                    Log.Error("queueing microphone audio failed", ex);
                }
            };

            capture.RecordingStopped += (_, args) =>
            {
                if (args.Exception is not null)
                {
                    Log.Error("microphone capture stopped", args.Exception);
                }
            };

            ISampleProvider chain = buffer.ToSampleProvider();
            if (chain.WaveFormat.Channels == 1 && targetFormat.Channels == 2)
            {
                chain = new MonoToStereoSampleProvider(chain);
            }

            if (chain.WaveFormat.SampleRate != targetFormat.SampleRate)
            {
                chain = new WdlResamplingSampleProvider(chain, targetFormat.SampleRate);
            }

            if (chain.WaveFormat.Channels != targetFormat.Channels)
            {
                Log.Warn($"microphone has {chain.WaveFormat.Channels} channels, output wants {targetFormat.Channels}");
                capture.Dispose();
                device.Dispose();
                return null;
            }

            var volume = new VolumeSampleProvider(chain) { Volume = _gain };

            lock (_gate)
            {
                _device = device;
                _capture = capture;
                _buffer = buffer;
                _volume = volume;
                CurrentDeviceId = device.ID;
            }

            capture.StartRecording();
            Log.Info($"microphone open: '{device.FriendlyName}' {inputFormat.SampleRate} Hz, " +
                     $"{inputFormat.Channels} ch, {inputFormat.Encoding}; mixed at {targetFormat.SampleRate} Hz");
            return volume;
        }
        catch (Exception ex)
        {
            Log.Error("starting microphone passthrough failed", ex);
            Stop();
            return null;
        }
    }

    public void Stop()
    {
        WasapiCapture? capture;
        MMDevice? device;
        lock (_gate)
        {
            capture = _capture;
            device = _device;
            _capture = null;
            _device = null;
            _buffer = null;
            _volume = null;
        }

        try
        {
            capture?.StopRecording();
            capture?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"stopping microphone capture failed: {ex.Message}");
        }

        device?.Dispose();
    }

    public void Dispose() => Stop();
}
