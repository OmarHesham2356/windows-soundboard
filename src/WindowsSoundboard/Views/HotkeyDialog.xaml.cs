using Microsoft.UI.Xaml.Controls;
using WindowsSoundboard.Models;
using WindowsSoundboard.Services;

namespace WindowsSoundboard.Views;

/// <summary>
/// Records a global hotkey. While it is open the keyboard hook captures the next
/// key press (with whatever modifiers are held), so combinations such as
/// Ctrl+Shift+F1 can be assigned.
/// </summary>
public sealed partial class HotkeyDialog : ContentDialog
{
    private readonly HotkeyService _hotkeys;
    private readonly SoundItem? _sound;
    private readonly string? _controlId;
    private readonly string? _original;
    private string _captured;

    /// <summary>New binding text, or null when the user asked to clear it.</summary>
    public string? Result { get; private set; }

    public bool Cleared { get; private set; }

    public HotkeyDialog(
        HotkeyService hotkeys,
        string target,
        string? current,
        SoundItem? sound = null,
        string? controlId = null)
    {
        InitializeComponent();

        _hotkeys = hotkeys;
        _sound = sound;
        _controlId = controlId;
        _original = current;
        _captured = current ?? string.Empty;
        Result = current;

        TargetText.Text = target;
        KeyText.Text = string.IsNullOrWhiteSpace(current) ? "Press a key combination…" : current;

        PrimaryButtonClick += (_, _) =>
        {
            Result = string.IsNullOrWhiteSpace(_captured) ? null : _captured;
            if (Result is null)
            {
                Cleared = true;
            }
        };

        SecondaryButtonClick += (_, _) =>
        {
            Cleared = true;
            Result = null;
        };

        Opened += OnOpened;
        Closed += OnClosed;
    }

    private void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        _hotkeys.Captured += OnSoundCaptured;
        _hotkeys.ControlCaptured += OnControlCaptured;
        _hotkeys.CaptureCancelled += OnCaptureCancelled;
        _hotkeys.BeginCapture(_sound, _controlId);
    }

    private void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        _hotkeys.CancelCapture();
        _hotkeys.Captured -= OnSoundCaptured;
        _hotkeys.ControlCaptured -= OnControlCaptured;
        _hotkeys.CaptureCancelled -= OnCaptureCancelled;
    }

    private void OnSoundCaptured(SoundItem sound, HotkeyBinding binding)
    {
        if (!ReferenceEquals(sound, _sound))
        {
            return;
        }

        Show(binding);
    }

    private void OnControlCaptured(string actionId, HotkeyBinding binding)
    {
        if (!string.Equals(actionId, _controlId, StringComparison.Ordinal))
        {
            return;
        }

        Show(binding);
    }

    private void Show(HotkeyBinding binding)
    {
        _captured = binding.Text;
        KeyText.Text = binding.Text;
        Result = binding.Text;
        Log.Info($"captured {binding.Text}");
    }

    private void OnCaptureCancelled()
    {
        KeyText.Text = string.IsNullOrWhiteSpace(_original) ? "Press a key combination…" : _original;
    }
}
