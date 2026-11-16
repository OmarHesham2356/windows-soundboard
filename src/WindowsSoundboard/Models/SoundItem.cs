using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WindowsSoundboard.Models;

/// <summary>One sound file in the library, as the UI sees it.</summary>
public sealed class SoundItem : INotifyPropertyChanged
{
    private string? _hotkey;
    private bool _isPlaying;
    private bool _isWaitingForKey;
    private double _gainDb;

    public SoundItem(string id, string path, string name)
    {
        Id = id;
        Path = path;
        Name = name;
    }

    public string Id { get; }

    public string Path { get; }

    public string Name { get; }

    /// <summary>Binding text such as "Ctrl+F1"; null when the sound has no key.</summary>
    public string? Hotkey
    {
        get => _hotkey;
        set
        {
            if (_hotkey == value)
            {
                return;
            }

            _hotkey = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HotkeyText));
        }
    }

    public string HotkeyText => _isWaitingForKey ? "press a key…" : _hotkey ?? "no key";

    /// <summary>Gain applied on playback so every sound lands at the same level.</summary>
    public double GainDb
    {
        get => _gainDb;
        set
        {
            if (Math.Abs(_gainDb - value) < 0.05)
            {
                return;
            }

            _gainDb = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GainText));
        }
    }

    public string GainText => Math.Abs(_gainDb) < 0.05 ? "0.0 dB" : _gainDb.ToString("+0.0;-0.0") + " dB";

    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (_isPlaying == value)
            {
                return;
            }

            _isPlaying = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PlayingVisibility));
        }
    }

    /// <summary>Drives the card's activity ring without a converter in XAML.</summary>
    public Microsoft.UI.Xaml.Visibility PlayingVisibility
        => _isPlaying ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public bool IsWaitingForKey
    {
        get => _isWaitingForKey;
        set
        {
            if (_isWaitingForKey == value)
            {
                return;
            }

            _isWaitingForKey = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HotkeyText));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
