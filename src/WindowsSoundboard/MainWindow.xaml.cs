using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using WindowsSoundboard.Models;
using WindowsSoundboard.Services;

namespace WindowsSoundboard;

public sealed partial class MainWindow : Window
{
    private readonly AppConfig _config;
    private readonly AudioEngine _engine = new();
    private readonly MicPassthrough _mic = new();
    private readonly HotkeyService _hotkeys = new();
    private readonly ObservableCollection<SoundItem> _sounds = [];
    private bool _suppressDeviceChange;
    private bool _suppressMicChange;
    private bool _suppressNormaliseChange;
    private int _libraryVersion;

    public MainWindow()
    {
        InitializeComponent();
        SystemBackdrop = new MicaBackdrop();

        _config = ConfigStore.Load();
        SoundGrid.ItemsSource = _sounds;

        _engine.AnyPlayingChanged += OnAnyPlayingChanged;
        _hotkeys.Triggered += OnHotkeyTriggered;
        _hotkeys.Captured += OnHotkeyCaptured;
        _hotkeys.CaptureCancelled += OnCaptureCancelled;

        LoadDevices();
        LoadMics();
        LoadSounds();
        UpdateModeButton();

        _suppressNormaliseChange = true;
        NormaliseSwitch.IsOn = _config.NormaliseLoudness;
        _suppressNormaliseChange = false;

        VolumeSlider.Value = Math.Clamp(_config.Volume * 100, 0, 150);
        _engine.SetVolume(_config.Volume);

        _hotkeys.Start();
        Closed += (_, _) => Shutdown();

        Log.Info("window ready");
    }

    private void LoadDevices()
    {
        var devices = _engine.ListOutputs();
        _suppressDeviceChange = true;
        DeviceCombo.ItemsSource = devices;

        var selected =
            devices.FirstOrDefault(device => !string.IsNullOrEmpty(_config.OutputDeviceId) && device.Id == _config.OutputDeviceId)
            ?? devices.FirstOrDefault(device => device.IsVirtualCable)
            ?? devices.FirstOrDefault();

        DeviceCombo.SelectedItem = selected;
        _suppressDeviceChange = false;

        if (selected is null)
        {
            StatusText.Text = "No audio output found.";
            Log.Warn("no audio outputs");
            return;
        }

        _config.OutputDeviceId = selected.Id;
        _engine.SetOutput(selected.Id);
        RefreshMic();

        StatusText.Text = selected.IsVirtualCable
            ? $"Playing into {selected.Name}"
            : "No virtual cable selected — other apps will not hear this. Pick a CABLE Input device.";
    }

    private void LoadSounds()
    {
        try
        {
            Directory.CreateDirectory(_config.SoundsFolder);
        }
        catch (Exception ex)
        {
            Log.Error("creating the sounds folder failed", ex);
        }

        _sounds.Clear();
        foreach (var item in SoundLibrary.Scan(_config.SoundsFolder))
        {
            if (_config.Hotkeys.TryGetValue(item.Id, out var hotkey))
            {
                item.Hotkey = hotkey;
            }

            _sounds.Add(item);
        }

        _hotkeys.SetBindings(_sounds);
        EmptyState.Visibility = _sounds.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Log.Info($"library: {_sounds.Count} sounds in '{_config.SoundsFolder}'");

        _libraryVersion++;
        _ = AnalyseLoudnessAsync(_libraryVersion);
    }

    /// <summary>
    /// Measures every sound once (cached by file size and write time) so playback can
    /// apply a gain that evens them out. Runs off the UI thread; results are applied
    /// back on it, and a reload mid-analysis discards the stale results.
    /// </summary>
    private async Task AnalyseLoudnessAsync(int version)
    {
        var items = _sounds.ToList();
        if (!_config.NormaliseLoudness)
        {
            foreach (var item in items)
            {
                item.GainDb = 0;
            }

            return;
        }

        var results = await Task.Run(() =>
        {
            var measured = new List<(SoundItem Item, double Gain, LoudnessCacheEntry Entry)>();
            foreach (var item in items)
            {
                try
                {
                    var info = new FileInfo(item.Path);
                    if (_config.Loudness.TryGetValue(item.Id, out var cached) &&
                        cached.Length == info.Length &&
                        cached.ModifiedTicks == info.LastWriteTimeUtc.Ticks)
                    {
                        measured.Add((item, cached.GainDb, cached));
                        continue;
                    }

                    var gain = LoudnessAnalyzer.MeasureGain(item.Path);
                    if (gain is null)
                    {
                        continue;
                    }

                    var entry = new LoudnessCacheEntry
                    {
                        Length = info.Length,
                        ModifiedTicks = info.LastWriteTimeUtc.Ticks,
                        GainDb = gain.Value,
                    };

                    measured.Add((item, gain.Value, entry));
                }
                catch (Exception ex)
                {
                    Log.Warn($"loudness skipped for {item.Name}: {ex.GetType().Name}");
                }
            }

            return measured;
        });

        if (version != _libraryVersion)
        {
            return;
        }

        var boosted = 0;
        var cut = 0;
        foreach (var (item, gain, entry) in results)
        {
            item.GainDb = gain;
            _config.Loudness[item.Id] = entry;
            if (gain > 0.5)
            {
                boosted++;
            }
            else if (gain < -0.5)
            {
                cut++;
            }
        }

        ConfigStore.Save(_config);
        Log.Info($"loudness: {results.Count} measured, {boosted} boosted, {cut} cut, target " +
                 $"{LoudnessAnalyzer.TargetRmsDb:0} dBFS");
    }

    private void UpdateModeButton()
    {
        ModeLabel.Text = _config.Mode.Label();
        ModeHint.Text = _config.Mode.Description();
        ModeIcon.Glyph = _config.Mode switch
        {
            TriggerMode.Overlap => "\uE8EE",
            TriggerMode.Stop => "\uE71A",
            _ => "\uE769",
        };
    }

    private void OnModeClicked(object sender, RoutedEventArgs e)
    {
        _config.Mode = _config.Mode.Next();
        ConfigStore.Save(_config);
        UpdateModeButton();
        StatusText.Text = _config.Mode.Description();
    }

    private void OnDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDeviceChange || DeviceCombo.SelectedItem is not AudioDevice device)
        {
            return;
        }

        _config.OutputDeviceId = device.Id;
        ConfigStore.Save(_config);
        _engine.SetOutput(device.Id);
        RefreshMic();
        StatusText.Text = $"Playing into {device.Name}";
    }

    private void LoadMics()
    {
        var devices = new List<MicDevice> { new(string.Empty, "(system default microphone)", false) };
        devices.AddRange(_mic.ListInputs());

        _suppressMicChange = true;
        MicCombo.ItemsSource = devices;
        var selected =
            devices.FirstOrDefault(device => !string.IsNullOrEmpty(_config.MicDeviceId) && device.Id == _config.MicDeviceId)
            ?? devices.FirstOrDefault(device => !device.IsVirtualCable && !string.IsNullOrEmpty(device.Id))
            ?? devices[0];

        MicCombo.SelectedItem = selected;
        MicSwitch.IsOn = _config.MicPassthrough;
        _suppressMicChange = false;

        if (selected.IsVirtualCable)
        {
            MicHint.Text = "This looks like a virtual cable — it would echo the soundboard back into itself.";
        }
        else
        {
            MicHint.Text = selected.Id.Length == 0 ? "Choose the microphone you talk into." : selected.Name;
        }
    }

    /// <summary>
    /// (Re)opens the microphone chain for the current output format. Called after every
    /// output change, because a new mixer needs the microphone added to it again.
    /// </summary>
    private void RefreshMic()
    {
        _engine.SetMicInput(null);
        _mic.Stop();

        if (!_config.MicPassthrough)
        {
            return;
        }

        // An empty id means "whatever Windows uses by default"; MicPassthrough falls back for us.
        var format = _engine.MixerFormat;
        if (format is null)
        {
            Log.Warn("microphone not started: no output stream yet");
            return;
        }

        var input = _mic.Start(_config.MicDeviceId, format);
        if (input is null)
        {
            StatusText.Text = "Microphone could not be opened.";
            return;
        }

        _engine.SetMicInput(input);
    }

    private void OnMicChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressMicChange || MicCombo.SelectedItem is not MicDevice device)
        {
            return;
        }

        _config.MicDeviceId = device.Id;
        ConfigStore.Save(_config);
        RefreshMic();
        MicHint.Text = device.Id.Length == 0 ? "Choose the microphone you talk into." : device.Name;
    }

    private void OnMicToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressMicChange)
        {
            return;
        }

        _config.MicPassthrough = MicSwitch.IsOn;
        ConfigStore.Save(_config);
        RefreshMic();
        StatusText.Text = MicSwitch.IsOn ? "Microphone mixed into the cable." : "Microphone passthrough off.";
    }

    private void OnVolumeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_config is null)
        {
            return;
        }

        _config.Volume = e.NewValue / 100.0;
        _engine.SetVolume(_config.Volume);
    }

    private void OnPlayClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SoundItem item)
        {
            Play(item);
        }
    }

    private void Play(SoundItem item)
    {
        // A click cannot be held, so a click in hold mode behaves like a normal press.
        var mode = _config.Mode.IsHold() ? TriggerMode.Stop : _config.Mode;
        _engine.Play(item.Path, mode, item.GainDb);
    }

    private void OnAssignClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SoundItem item)
        {
            return;
        }

        foreach (var sound in _sounds)
        {
            sound.IsWaitingForKey = false;
        }

        item.IsWaitingForKey = true;
        _hotkeys.BeginCapture(item);
        StatusText.Text = $"Press the key for \u201c{item.Name}\u201d (Escape cancels)";
    }

    private void OnHotkeyCaptured(SoundItem item, HotkeyBinding binding)
    {
        item.Hotkey = binding.Text;
        item.IsWaitingForKey = false;
        _config.Hotkeys[item.Id] = binding.Text;
        ConfigStore.Save(_config);
        _hotkeys.SetBindings(_sounds);
        StatusText.Text = $"{item.Name} \u2192 {binding.Text}";
    }

    private void OnCaptureCancelled()
    {
        foreach (var sound in _sounds)
        {
            sound.IsWaitingForKey = false;
        }

        StatusText.Text = "Key capture cancelled.";
    }

    private void OnHotkeyTriggered(SoundItem item, bool isDown)
    {
        if (_config.Mode.IsHold())
        {
            if (isDown)
            {
                _engine.HoldStart(item.Path, item.GainDb);
            }
            else
            {
                _engine.HoldStop(item.Path);
            }

            return;
        }

        if (isDown)
        {
            _engine.Play(item.Path, _config.Mode, item.GainDb);
        }
    }

    private void OnNormaliseToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressNormaliseChange)
        {
            return;
        }

        _config.NormaliseLoudness = NormaliseSwitch.IsOn;
        ConfigStore.Save(_config);
        _libraryVersion++;
        _ = AnalyseLoudnessAsync(_libraryVersion);
        StatusText.Text = NormaliseSwitch.IsOn
            ? $"Levelling every sound to {LoudnessAnalyzer.TargetRmsDb:0} dBFS."
            : "Loudness levelling off.";
    }

    private void OnAnyPlayingChanged(bool anyPlaying)
    {
        // Placeholder for a future per-sound indicator; keeps the hook alive for the UI.
    }

    private async void OnChooseFolderClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null)
            {
                return;
            }

            _config.SoundsFolder = folder.Path;
            ConfigStore.Save(_config);
            LoadSounds();
            StatusText.Text = $"{_sounds.Count} sounds in {folder.Path}";
        }
        catch (Exception ex)
        {
            Log.Error("folder picker failed", ex);
            StatusText.Text = "Could not open the folder picker.";
        }
    }

    private void Shutdown()
    {
        ConfigStore.Save(_config);
        _hotkeys.Dispose();
        _mic.Dispose();
        _engine.Dispose();
        Log.Info("shutdown");
    }
}
