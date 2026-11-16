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
    private readonly HotkeyService _hotkeys = new();
    private readonly ObservableCollection<SoundItem> _sounds = [];
    private bool _suppressDeviceChange;

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
        LoadSounds();
        UpdateModeButton();

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
        StatusText.Text = $"Playing into {device.Name}";
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
        _engine.Play(item.Path, mode);
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
                _engine.HoldStart(item.Path);
            }
            else
            {
                _engine.HoldStop(item.Path);
            }

            return;
        }

        if (isDown)
        {
            _engine.Play(item.Path, _config.Mode);
        }
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
        _engine.Dispose();
        Log.Info("shutdown");
    }
}
