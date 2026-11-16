using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using WindowsSoundboard.Models;
using WindowsSoundboard.Services;
using WindowsSoundboard.Views;

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
    private bool _suppressVolumeChange;
    private int _libraryVersion;
    private string _micSignature = string.Empty;
    private string? _lastPlayedPath;
    private TrayIcon? _tray;
    private SettingsWindow? _settings;
    private bool _exitRequested;
    private bool _windowHidden;

    public MainWindow()
    {
        InitializeComponent();
        SystemBackdrop = new MicaBackdrop();

        _config = ConfigStore.Load();
        SoundGrid.ItemsSource = _sounds;
        SoundList.ItemsSource = _sounds;

        _engine.AnyPlayingChanged += OnAnyPlayingChanged;
        _engine.SoundPlaybackChanged += OnSoundPlaybackChanged;
        _engine.SoundFinished += OnSoundFinished;
        _hotkeys.Triggered += OnHotkeyTriggered;
        _hotkeys.ControlTriggered += OnControlHotkey;
        _hotkeys.Captured += OnHotkeyCaptured;

        _engine.SetVolume(_config.Volume);
        _engine.SetBoostDb(_config.LoudnessBoostDb);
        _engine.SetMuted(_config.Muted);
        _mic.BufferMilliseconds = _config.MicBufferMilliseconds;
        _mic.SetGain(_config.Muted ? 0 : _config.MicVolume);

        LoadDevices();
        LoadMics();
        LoadSounds();
        ApplyTheme();
        ApplyListStyle();
        UpdateModeButton();
        UpdatePlayModeButton();
        SyncControls();

        _hotkeys.Start();
        UpdateTray();
        AppWindow.Closing += OnWindowClosing;
        Closed += (_, _) => Shutdown();

        Log.Info("window ready");
    }

    // ---------------------------------------------------------------- settings

    /// <summary>
    /// Re-applies everything the options window can change. Called after every
    /// edit there, so settings take effect without a restart.
    /// </summary>
    internal void ApplySettings()
    {
        var outputChanged =
            !string.IsNullOrEmpty(_config.OutputDeviceId) &&
            !string.Equals(_engine.CurrentDeviceId, _config.OutputDeviceId, StringComparison.Ordinal);

        if (outputChanged)
        {
            _engine.SetOutput(_config.OutputDeviceId);
        }

        _engine.SetVolume(_config.Volume);
        _engine.SetBoostDb(_config.LoudnessBoostDb);
        _engine.SetMuted(_config.Muted);
        _mic.BufferMilliseconds = _config.MicBufferMilliseconds;
        _mic.SetGain(_config.Muted ? 0 : _config.MicVolume);

        var signature = $"{_config.MicPassthrough}|{_config.MicDeviceId}|{_config.MicBufferMilliseconds}";
        if (outputChanged || signature != _micSignature)
        {
            RefreshMic();
        }

        ApplyTheme();
        ApplyListStyle();
        UpdateModeButton();
        UpdatePlayModeButton();
        RebindHotkeys();

        var normaliseChanged = NormaliseSwitch.IsOn != _config.NormaliseLoudness;
        if (normaliseChanged)
        {
            _libraryVersion++;
            _ = AnalyseLoudnessAsync(_libraryVersion);
        }

        UpdateTray();
        SyncControls();

        // One line that proves what actually reached the engine, the microphone and
        // the UI, rather than what the config merely says.
        Log.Info(
            $"settings applied: output='{_engine.CurrentDeviceId}' " +
            $"engine[volume={_engine.Volume:0.00} boost={_engine.BoostDb:0.0}dB muted={_engine.IsMuted}] " +
            $"trigger={_config.Mode} play={_config.PlayMode} normalise={_config.NormaliseLoudness} " +
            $"theme={_config.Theme}/{Root.RequestedTheme} list={_config.ListStyle} " +
            $"cards={SoundGrid.Visibility} listView={SoundList.Visibility} " +
            $"tray={(_tray is not null ? "on" : "off")} closeToTray={_config.CloseToTray} " +
            $"mic[running={_mic.IsRunning} gain={_mic.Gain:0.00} buffer={_mic.BufferMilliseconds}ms] " +
            $"soundHotkeys={_sounds.Count(sound => sound.Hotkey is not null)} " +
            $"controlHotkeys={_config.ControlHotkeys.Count} shared={_config.MultiSoundHotkeys}/{_config.GroupMode}");
    }

    private void ApplyTheme()
    {
        Root.RequestedTheme = _config.Theme switch
        {
            AppTheme.Dark => ElementTheme.Dark,
            AppTheme.Light => ElementTheme.Light,
            _ => ElementTheme.Default,
        };
    }

    private void ApplyListStyle()
    {
        var compact = _config.ListStyle == ListStyle.Compact;
        SoundGrid.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        SoundList.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = _sounds.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RebindHotkeys()
        => _hotkeys.SetBindings(_sounds, _config.ControlHotkeys, _config.MultiSoundHotkeys, _config.GroupMode);

    private void SyncControls()
    {
        _suppressVolumeChange = true;
        VolumeSlider.Value = Math.Clamp(_config.Volume * 100, 0, 150);
        _suppressVolumeChange = false;

        _suppressNormaliseChange = true;
        NormaliseSwitch.IsOn = _config.NormaliseLoudness;
        _suppressNormaliseChange = false;

        _suppressMicChange = true;
        MicSwitch.IsOn = _config.MicPassthrough;
        if (MicCombo.ItemsSource is IEnumerable<MicDevice> mics)
        {
            var match = mics.FirstOrDefault(device => device.Id == _config.MicDeviceId);
            if (match is not null)
            {
                MicCombo.SelectedItem = match;
            }
        }

        _suppressMicChange = false;

        _suppressDeviceChange = true;
        if (DeviceCombo.ItemsSource is IEnumerable<AudioDevice> devices)
        {
            var match = devices.FirstOrDefault(device => device.Id == _config.OutputDeviceId);
            if (match is not null)
            {
                DeviceCombo.SelectedItem = match;
            }
        }

        _suppressDeviceChange = false;
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

        RebindHotkeys();
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

    // ------------------------------------------------------------- transport

    private void UpdateModeButton()
    {
        ModeLabel.Text = _config.Mode.Label();
        ModeHint.Text = _config.Mode.Description();
        ModeIcon.Glyph = _config.Mode switch
        {
            TriggerMode.Overlap => "\uE8EE",
            TriggerMode.Stop => "\uE71A",
            TriggerMode.PushToLoop => "\uE8EF",
            _ => "\uE769",
        };
    }

    private void UpdatePlayModeButton() => PlayModeLabel.Text = _config.PlayMode.Label();

    private void OnModeClicked(object sender, RoutedEventArgs e)
    {
        _config.Mode = _config.Mode.Next();
        ConfigStore.Save(_config);
        UpdateModeButton();
        StatusText.Text = _config.Mode.Description();
    }

    private void OnPlayModeClicked(object sender, RoutedEventArgs e) => CyclePlayMode();

    private void CyclePlayMode()
    {
        _config.PlayMode = _config.PlayMode.Next();
        ConfigStore.Save(_config);
        UpdatePlayModeButton();
        StatusText.Text = _config.PlayMode.Description();
    }

    private void OnStopAllClicked(object sender, RoutedEventArgs e) => StopAllSounds();

    private void StopAllSounds()
    {
        _engine.StopAll();
        foreach (var item in _sounds)
        {
            item.IsPlaying = false;
        }

        StatusText.Text = "Stopped.";
    }

    private void TogglePause()
    {
        if (_engine.IsPaused)
        {
            _engine.ResumeSounds();
            StatusText.Text = "Resumed.";
            return;
        }

        _engine.PauseSounds();
        StatusText.Text = _engine.IsPaused ? "Paused." : "Nothing is playing.";
    }

    private void Play(SoundItem item)
    {
        // A click cannot be held, so a click in hold mode behaves like a normal press.
        _lastPlayedPath = item.Path;
        var mode = _config.Mode.IsHold() ? TriggerMode.Stop : _config.Mode;
        _engine.Play(item.Path, mode, item.GainDb, loop: _config.PlayMode == PlayMode.Loop);
    }

    private void PlayRelative(int delta)
    {
        if (_sounds.Count == 0)
        {
            return;
        }

        var index = _sounds
            .Select((item, position) => (item, position))
            .FirstOrDefault(pair => string.Equals(pair.item.Path, _lastPlayedPath, StringComparison.OrdinalIgnoreCase))
            .position;

        if (string.IsNullOrEmpty(_lastPlayedPath))
        {
            index = delta > 0 ? -1 : 0;
        }

        var next = ((index + delta) % _sounds.Count + _sounds.Count) % _sounds.Count;
        Play(_sounds[next]);
        StatusText.Text = $"Playing {_sounds[next].Name}.";
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
        _tray?.SetToolTip($"Windows Soundboard \u2014 {device.Name}");
        StatusText.Text = $"Playing into {device.Name}";
    }

    // ----------------------------------------------------------- microphone

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
        _micSignature = $"{_config.MicPassthrough}|{_config.MicDeviceId}|{_config.MicBufferMilliseconds}";
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

    private void ToggleMicPassthrough() => ApplyMicPassthrough(!_config.MicPassthrough);

    private void ApplyMicPassthrough(bool enabled)
    {
        _config.MicPassthrough = enabled;
        ConfigStore.Save(_config);
        SyncControls();
        RefreshMic();
        StatusText.Text = enabled ? "Microphone mixed into the cable." : "Microphone passthrough off.";
    }

    // -------------------------------------------------------------- hotkeys

    private void OnPlayClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SoundItem item)
        {
            Play(item);
        }
    }

    private void OnSoundItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SoundItem item)
        {
            Play(item);
        }
    }

    private void OnSoundRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not SoundItem item)
        {
            return;
        }

        var flyout = new MenuFlyout();

        var play = new MenuFlyoutItem { Text = "Play" };
        play.Click += (_, _) => Play(item);
        flyout.Items.Add(play);

        var assign = new MenuFlyoutItem { Text = item.Hotkey is null ? "Set hotkey…" : "Update hotkey…" };
        assign.Click += async (_, _) => await EditHotkeyAsync(item);
        flyout.Items.Add(assign);

        if (item.Hotkey is not null)
        {
            var clear = new MenuFlyoutItem { Text = "Clear hotkey" };
            clear.Click += (_, _) => ClearHotkey(item);
            flyout.Items.Add(clear);
        }

        var reveal = new MenuFlyoutItem { Text = "Show in folder" };
        reveal.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.Path}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Error("opening the folder failed", ex);
            }
        };
        flyout.Items.Add(reveal);

        if (sender is FrameworkElement element)
        {
            flyout.ShowAt(element, new FlyoutShowOptions { Position = e.GetPosition(element) });
        }
    }

    private void OnAssignClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SoundItem item)
        {
            _ = EditHotkeyAsync(item);
        }
    }

    private async Task EditHotkeyAsync(SoundItem item)
    {
        _hotkeys.CancelCapture();

        var dialog = new HotkeyDialog(
            _hotkeys,
            $"Sound: {item.Name}",
            item.Hotkey,
            sound: item)
        {
            XamlRoot = Content.XamlRoot,
        };

        await dialog.ShowAsync();

        if (dialog.Cleared)
        {
            ClearHotkey(item);
            return;
        }

        if (string.IsNullOrWhiteSpace(dialog.Result))
        {
            return;
        }

        item.Hotkey = dialog.Result;
        _config.Hotkeys[item.Id] = dialog.Result!;
        ConfigStore.Save(_config);
        RebindHotkeys();
        StatusText.Text = $"{item.Name} \u2192 {dialog.Result}";
    }

    private void ClearHotkey(SoundItem item)
    {
        item.Hotkey = null;
        _config.Hotkeys.Remove(item.Id);
        ConfigStore.Save(_config);
        RebindHotkeys();
        StatusText.Text = $"{item.Name} has no hotkey.";
    }

    private void OnHotkeyCaptured(SoundItem item, HotkeyBinding binding)
    {
        // The dialog owns capture; this keeps the model in step if one is open elsewhere.
        item.Hotkey = binding.Text;
    }

    private void OnHotkeyTriggered(SoundItem item, bool isDown)
    {
        if (_config.Mode.IsHold())
        {
            if (isDown)
            {
                _lastPlayedPath = item.Path;
                _engine.HoldStart(
                    item.Path,
                    item.GainDb,
                    loop: _config.Mode.LoopsWhileHeld() || _config.PlayMode == PlayMode.Loop);
            }
            else
            {
                _engine.HoldStop(item.Path);
            }

            return;
        }

        if (isDown)
        {
            Play(item);
        }
    }

    private void OnControlHotkey(string actionId, bool isDown)
    {
        if (!isDown)
        {
            return;
        }

        Log.Info($"control hotkey: {actionId}");

        switch (actionId)
        {
            case ControlActions.PlayPause:
                TogglePause();
                break;
            case ControlActions.StopAll:
                StopAllSounds();
                break;
            case ControlActions.PreviousSound:
                PlayRelative(-1);
                break;
            case ControlActions.NextSound:
                PlayRelative(1);
                break;
            case ControlActions.MuteOutput:
                ApplyMute(!_config.Muted);
                break;
            case ControlActions.MuteMic:
                ToggleMicPassthrough();
                break;
            case ControlActions.CycleTriggerMode:
                _config.Mode = _config.Mode.Next();
                ConfigStore.Save(_config);
                UpdateModeButton();
                StatusText.Text = _config.Mode.Label();
                break;
            case ControlActions.CyclePlayMode:
                CyclePlayMode();
                break;
        }
    }

    // ---------------------------------------------------------------- output

    private void OnVolumeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressVolumeChange)
        {
            return;
        }

        _config.Volume = e.NewValue / 100.0;
        _engine.SetVolume(_config.Volume);
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
        // Per-sound state arrives through OnSoundPlaybackChanged.
    }

    /// <summary>
    /// Marks the matching cards as playing or finished. Endings are raised from the
    /// audio thread, so every update hops to the UI thread.
    /// </summary>
    private void OnSoundPlaybackChanged(string path, bool playing)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            foreach (var item in _sounds)
            {
                if (string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    item.IsPlaying = playing;
                }
            }
        });
    }

    /// <summary>Continue play mode: a sound that ran out hands over to the next one.</summary>
    private void OnSoundFinished(string path)
    {
        if (_config.PlayMode != PlayMode.Continue)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_config.PlayMode != PlayMode.Continue || _sounds.Count == 0)
            {
                return;
            }

            var index = _sounds
                .Select((item, position) => (item, position))
                .FirstOrDefault(pair => string.Equals(pair.item.Path, path, StringComparison.OrdinalIgnoreCase))
                .position;

            var next = (_sounds.FirstOrDefault(item =>
                string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)) is null)
                ? 0
                : (index + 1) % _sounds.Count;

            Play(_sounds[next]);
        });
    }

    private async void OnChooseFolderClicked(object sender, RoutedEventArgs e) => await ChangeFolderAsync();

    internal async Task ChangeFolderAsync()
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

    internal void ReloadLibrary() => LoadSounds();

    internal string LibrarySummary => $"{_sounds.Count} sounds";

    internal string MicSummary => _mic.IsRunning
        ? "Passthrough is running."
        : _config.MicPassthrough
            ? "Waiting for a microphone…"
            : "Passthrough is off.";

    // ------------------------------------------------------------------ tray

    private void OnSettingsClicked(object sender, RoutedEventArgs e) => ShowSettings();

    internal void ShowSettings()
    {
        if (_settings is not null)
        {
            _settings.Activate();
            return;
        }

        _settings = new SettingsWindow(this, _config, _engine, _mic, _hotkeys);
        _settings.Closed += (_, _) => _settings = null;
        _settings.Activate();
    }

    private void UpdateTray()
    {
        if (_config.TrayEnabled && _tray is null)
        {
            CreateTray();
            return;
        }

        if (!_config.TrayEnabled && _tray is not null)
        {
            _tray.Dispose();
            _tray = null;

            if (_windowHidden)
            {
                AppWindow.Show();
                _windowHidden = false;
            }
        }
    }

    private void CreateTray()
    {
        try
        {
            _tray = new TrayIcon(
                "Windows Soundboard",
                ToggleWindowVisibility,
                ToggleMute,
                RequestExit,
                () => !_windowHidden,
                () => _engine.IsMuted);
        }
        catch (Exception ex)
        {
            // Without a tray icon closing the window must still quit, or the app
            // would be unreachable.
            Log.Error("tray icon unavailable; closing the window will quit", ex);
            _tray = null;
        }
    }

    private void ToggleWindowVisibility()
    {
        if (_windowHidden)
        {
            AppWindow.Show();
            Activate();
            _windowHidden = false;
        }
        else
        {
            AppWindow.Hide();
            _windowHidden = true;
        }
    }

    private void ToggleMute() => ApplyMute(!_config.Muted);

    private void ApplyMute(bool muted)
    {
        _config.Muted = muted;
        _engine.SetMuted(muted);
        _mic.SetGain(muted ? 0.0 : _config.MicVolume);
        ConfigStore.Save(_config);
        Log.Info(muted ? "muted" : "unmuted");
        StatusText.Text = muted ? "Output muted." : "Output unmuted.";
    }

    private void RequestExit()
    {
        _exitRequested = true;
        Close();
    }

    private void OnWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_exitRequested || _tray is null || !_config.CloseToTray)
        {
            return;
        }

        // Hide instead of quitting: hotkeys must keep working while the window is away.
        args.Cancel = true;
        AppWindow.Hide();
        _windowHidden = true;
        _tray.SetToolTip("Windows Soundboard \u2014 running in the tray");
    }

    private void Shutdown()
    {
        _tray?.Dispose();
        ConfigStore.Save(_config);
        _hotkeys.Dispose();
        _mic.Dispose();
        _engine.Dispose();
        Log.Info("shutdown");
    }
}
