using System.Diagnostics;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WindowsSoundboard.Models;
using WindowsSoundboard.Services;

namespace WindowsSoundboard.Views;

/// <summary>
/// The options window. Everything here edits the live config and then asks the
/// main window to re-apply it, so a change takes effect immediately.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private readonly MainWindow _owner;
    private readonly AppConfig _config;
    private readonly AudioEngine _engine;
    private readonly MicPassthrough _mic;
    private readonly HotkeyService _hotkeys;
    private bool _loading = true;

    private static readonly int[] LatencyMilliseconds = [400, 200, 100];

    public SettingsWindow(
        MainWindow owner,
        AppConfig config,
        AudioEngine engine,
        MicPassthrough mic,
        HotkeyService hotkeys)
    {
        InitializeComponent();

        _owner = owner;
        _config = config;
        _engine = engine;
        _mic = mic;
        _hotkeys = hotkeys;

        Title = "Options";
        AppWindow.Resize(new SizeInt32(940, 760));
        SystemBackdrop = new MicaBackdrop();
        Root.RequestedTheme = _config.Theme switch
        {
            AppTheme.Dark => ElementTheme.Dark,
            AppTheme.Light => ElementTheme.Light,
            _ => ElementTheme.Default,
        };

        BuildControlHotkeys();
        Populate();
        Nav.SelectedIndex = 0;
        _loading = false;

        Activated += (_, _) => RefreshStatus();
        Log.Info("options window ready");
    }

    private void Populate()
    {
        ThemeCombo.ItemsSource = new[] { "Follow Windows", "Dark", "Light" };
        ThemeCombo.SelectedIndex = (int)_config.Theme;

        ListStyleCombo.ItemsSource = new[] { "Cards", "Compact list" };
        ListStyleCombo.SelectedIndex = (int)_config.ListStyle;

        TriggerCombo.ItemsSource = new[] { "Play & Overlap", "Play & Stop", "Push to Loop", "Play & Hold" };
        TriggerCombo.SelectedIndex = (int)_config.Mode;
        TriggerHint.Text = _config.Mode.Description();

        PlayModeCombo.ItemsSource = new[] { "Default", "Loop", "Continue" };
        PlayModeCombo.SelectedIndex = (int)_config.PlayMode;
        PlayModeHint.Text = _config.PlayMode.Description();

        var outputs = _engine.ListOutputs();
        OutputCombo.ItemsSource = outputs;
        OutputCombo.SelectedItem =
            outputs.FirstOrDefault(device => device.Id == _config.OutputDeviceId)
            ?? outputs.FirstOrDefault(device => device.IsVirtualCable)
            ?? outputs.FirstOrDefault();

        var mics = new List<MicDevice> { new(string.Empty, "(system default microphone)", false) };
        mics.AddRange(_mic.ListInputs());
        MicCombo.ItemsSource = mics;
        MicCombo.SelectedItem =
            mics.FirstOrDefault(device => device.Id == _config.MicDeviceId)
            ?? mics.FirstOrDefault();

        LatencyCombo.ItemsSource = new[]
        {
            "Balanced (400 ms)",
            "Low latency (200 ms)",
            "Ultra low latency (100 ms)",
        };
        LatencyCombo.SelectedIndex = Math.Max(0, Array.IndexOf(LatencyMilliseconds, _config.MicBufferMilliseconds));

        GroupModeCombo.ItemsSource = new[]
        {
            "Play the same sound",
            "Play the next sound",
            "Play a random sound",
        };
        GroupModeCombo.SelectedIndex = (int)_config.GroupMode;

        VolumeSlider.Value = Math.Clamp(_config.Volume * 100, 0, 150);
        BoostSlider.Value = Math.Clamp(_config.LoudnessBoostDb, 0, 30);
        MicVolumeSlider.Value = Math.Clamp(_config.MicVolume * 100, 0, 200);

        NormaliseSwitch.IsOn = _config.NormaliseLoudness;
        MuteSwitch.IsOn = _config.Muted;
        MicSwitch.IsOn = _config.MicPassthrough;
        MultiSoundSwitch.IsOn = _config.MultiSoundHotkeys;
        TraySwitch.IsOn = _config.TrayEnabled;
        CloseToTraySwitch.IsOn = _config.CloseToTray;

        var version = typeof(App).Assembly.GetName().Version;
        VersionText.Text = $"Version {version?.ToString(3) ?? "0.1.0"}";
        PathsText.Text = $"Settings: {AppPaths.ConfigFile}\nLog: {Log.FilePath}";

        RefreshStatus();
    }

    private void RefreshStatus()
    {
        FolderText.Text = _config.SoundsFolder;
        LibraryCountText.Text = $"{_owner.LibrarySummary} — rescan after adding files";
        MicStatusText.Text = _owner.MicSummary;
        VolumeText.Text = $"{_config.Volume * 100:0}%";
        BoostText.Text = $"{_config.LoudnessBoostDb:0.0} dB";
        MicVolumeText.Text = $"{_config.MicVolume * 100:0}%";
    }

    private void Save() => ConfigStore.Save(_config);

    // -------------------------------------------------------------------- nav

    private void OnNavChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GeneralPage is null)
        {
            return;
        }

        var tag = (Nav.SelectedItem as ListViewItem)?.Tag as string ?? "general";

        GeneralPage.Visibility = tag == "general" ? Visibility.Visible : Visibility.Collapsed;
        PlaybackPage.Visibility = tag == "playback" ? Visibility.Visible : Visibility.Collapsed;
        MicrophonePage.Visibility = tag == "microphone" ? Visibility.Visible : Visibility.Collapsed;
        HotkeysPage.Visibility = tag == "hotkeys" ? Visibility.Visible : Visibility.Collapsed;
        TrayPage.Visibility = tag == "tray" ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;

        RefreshStatus();
    }

    // ---------------------------------------------------------------- general

    private async void OnChangeFolderClicked(object sender, RoutedEventArgs e)
    {
        await _owner.ChangeFolderAsync();
        RefreshStatus();
    }

    private void OnRescanClicked(object sender, RoutedEventArgs e)
    {
        _owner.ReloadLibrary();
        RefreshStatus();
    }

    private void OnOpenFolderClicked(object sender, RoutedEventArgs e) => Open(_config.SoundsFolder);

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeCombo.SelectedIndex < 0)
        {
            return;
        }

        _config.Theme = (AppTheme)ThemeCombo.SelectedIndex;
        Save();
        Root.RequestedTheme = _config.Theme switch
        {
            AppTheme.Dark => ElementTheme.Dark,
            AppTheme.Light => ElementTheme.Light,
            _ => ElementTheme.Default,
        };
        _owner.ApplySettings();
    }

    private void OnListStyleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ListStyleCombo.SelectedIndex < 0)
        {
            return;
        }

        _config.ListStyle = (ListStyle)ListStyleCombo.SelectedIndex;
        Save();
        _owner.ApplySettings();
    }

    // --------------------------------------------------------------- playback

    private void OnTriggerChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || TriggerCombo.SelectedIndex < 0)
        {
            return;
        }

        _config.Mode = (TriggerMode)TriggerCombo.SelectedIndex;
        TriggerHint.Text = _config.Mode.Description();
        Save();
        _owner.ApplySettings();
    }

    private void OnPlayModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || PlayModeCombo.SelectedIndex < 0)
        {
            return;
        }

        _config.PlayMode = (PlayMode)PlayModeCombo.SelectedIndex;
        PlayModeHint.Text = _config.PlayMode.Description();
        Save();
        _owner.ApplySettings();
    }

    private void OnOutputChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || OutputCombo.SelectedItem is not AudioDevice device)
        {
            return;
        }

        _config.OutputDeviceId = device.Id;
        Save();
        _owner.ApplySettings();
    }

    private void OnVolumeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _config.Volume = e.NewValue / 100.0;
        VolumeText.Text = $"{e.NewValue:0}%";
        _engine.SetVolume(_config.Volume);
        Save();
    }

    private void OnBoostChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _config.LoudnessBoostDb = e.NewValue;
        BoostText.Text = $"{e.NewValue:0.0} dB";
        _engine.SetBoostDb(_config.LoudnessBoostDb);
        Save();
    }

    private void OnNormaliseToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _config.NormaliseLoudness = NormaliseSwitch.IsOn;
        Save();
        _owner.ApplySettings();
    }

    private void OnMuteToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _config.Muted = MuteSwitch.IsOn;
        Save();
        _owner.ApplySettings();
    }

    // ------------------------------------------------------------- microphone

    private void OnMicToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _config.MicPassthrough = MicSwitch.IsOn;
        Save();
        _owner.ApplySettings();
        RefreshStatus();
    }

    private void OnMicChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || MicCombo.SelectedItem is not MicDevice device)
        {
            return;
        }

        _config.MicDeviceId = device.Id;
        Save();
        _owner.ApplySettings();
        RefreshStatus();
    }

    private void OnLatencyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LatencyCombo.SelectedIndex < 0)
        {
            return;
        }

        _config.MicBufferMilliseconds = LatencyMilliseconds[LatencyCombo.SelectedIndex];
        Save();
        _owner.ApplySettings();
        RefreshStatus();
    }

    private void OnMicVolumeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _config.MicVolume = e.NewValue / 100.0;
        MicVolumeText.Text = $"{e.NewValue:0}%";
        Save();
        _owner.ApplySettings();
    }

    // ---------------------------------------------------------------- hotkeys

    private void BuildControlHotkeys()
    {
        ControlHotkeyPanel.Children.Clear();

        foreach (var action in ControlActions.All)
        {
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var labels = new StackPanel { Spacing = 2 };
            labels.Children.Add(new TextBlock { Text = action.Title, FontWeight = FontWeights.SemiBold });
            labels.Children.Add(new TextBlock
            {
                Text = action.Subtitle,
                FontSize = 12,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
            });
            row.Children.Add(labels);

            var key = new TextBlock
            {
                Text = _config.GetControlHotkey(action.Id) ?? "no key",
                MinWidth = 96,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.85,
            };
            Grid.SetColumn(key, 1);
            row.Children.Add(key);

            var record = new Button { Content = "Record", Tag = action.Id };
            record.Click += OnRecordControlClicked;
            Grid.SetColumn(record, 2);
            row.Children.Add(record);

            var clear = new Button { Content = "Clear", Tag = action.Id };
            clear.Click += OnClearControlClicked;
            Grid.SetColumn(clear, 3);
            row.Children.Add(clear);

            ControlHotkeyPanel.Children.Add(row);
        }
    }

    private async void OnRecordControlClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string actionId)
        {
            return;
        }

        var action = ControlActions.All.First(item => item.Id == actionId);
        var current = _config.GetControlHotkey(actionId);

        var dialog = new HotkeyDialog(_hotkeys, action.Title, current, controlId: actionId)
        {
            XamlRoot = Root.XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.None)
        {
            return;
        }

        _config.SetControlHotkey(actionId, dialog.Cleared ? null : dialog.Result);
        Save();
        _owner.ApplySettings();
        BuildControlHotkeys();
    }

    private void OnClearControlClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string actionId)
        {
            return;
        }

        _config.SetControlHotkey(actionId, null);
        Save();
        _owner.ApplySettings();
        BuildControlHotkeys();
    }

    private void OnMultiSoundToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _config.MultiSoundHotkeys = MultiSoundSwitch.IsOn;
        Save();
        _owner.ApplySettings();
    }

    private void OnGroupModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || GroupModeCombo.SelectedIndex < 0)
        {
            return;
        }

        _config.GroupMode = (GroupMode)GroupModeCombo.SelectedIndex;
        Save();
        _owner.ApplySettings();
    }

    // ------------------------------------------------------------------- tray

    private void OnTrayToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _config.TrayEnabled = TraySwitch.IsOn;
        Save();
        _owner.ApplySettings();
    }

    private void OnCloseToTrayToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _config.CloseToTray = CloseToTraySwitch.IsOn;
        Save();
    }

    // ------------------------------------------------------------------ about

    private void OnOpenDataFolderClicked(object sender, RoutedEventArgs e) => Open(AppPaths.DataFolder);

    private void OnOpenLogClicked(object sender, RoutedEventArgs e) => Open(Log.FilePath);

    private static void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"opening '{path}' failed", ex);
        }
    }
}
