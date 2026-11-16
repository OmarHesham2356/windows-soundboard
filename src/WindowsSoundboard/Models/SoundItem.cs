using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WindowsSoundboard.Models;

/// <summary>One sound file in the library, as the UI sees it.</summary>
public sealed class SoundItem : INotifyPropertyChanged
{
    private string? _hotkey;
    private bool _isPlaying;
    private bool _isWaitingForKey;

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
        }
    }

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
