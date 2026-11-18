using System.Runtime.InteropServices;
using WindowsSoundboard.Models;

namespace WindowsSoundboard.Services;

/// <summary>
/// A global low-level keyboard hook. It runs on the UI thread's message loop, so
/// events arrive on the UI thread. Bound keys are swallowed, so pressing a pad key
/// does not also type into whatever application has focus.
///
/// A binding is a key plus any combination of Ctrl, Alt, Shift and Win. The same
/// key can serve several sounds when they are allowed to share a hotkey, and keys
/// can also be bound to the app's own controls (stop all, next sound, …).
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private sealed class Entry
    {
        public required HotkeyBinding Binding { get; init; }

        public SoundItem? Sound { get; init; }

        public string? ControlId { get; init; }
    }

    private readonly Native.LowLevelKeyboardProc _callback;
    private readonly Dictionary<uint, List<Entry>> _bindings = [];
    private readonly Dictionary<uint, Entry> _held = [];
    private readonly Dictionary<string, int> _groupIndex = [];
    private readonly Dictionary<string, string> _groupLast = [];

    private IntPtr _hook;
    private bool _capturing;
    private SoundItem? _captureSound;
    private string? _captureControl;
    private bool _multiSounds;
    private GroupMode _groupMode = GroupMode.Same;

    public HotkeyService()
    {
        // Keep the delegate in a field: the OS holds only a native pointer to it.
        _callback = HookCallback;
    }

    /// <summary>Fires on key down (true) and key up (false) for a bound sound.</summary>
    public event Action<SoundItem, bool>? Triggered;

    /// <summary>Fires on key down (true) and key up (false) for a bound control action.</summary>
    public event Action<string, bool>? ControlTriggered;

    /// <summary>Fires when a key was captured for a sound that was waiting for one.</summary>
    public event Action<SoundItem, HotkeyBinding>? Captured;

    /// <summary>Fires when a key was captured for a control action.</summary>
    public event Action<string, HotkeyBinding>? ControlCaptured;

    /// <summary>Fires when capture was abandoned with Escape.</summary>
    public event Action? CaptureCancelled;

    public bool IsRunning => _hook != IntPtr.Zero;

    public bool IsCapturing => _capturing;

    public void Start()
    {
        if (_hook != IntPtr.Zero)
        {
            return;
        }

        _hook = Native.SetWindowsHookEx(
            Native.WH_KEYBOARD_LL,
            _callback,
            Native.GetModuleHandle(null),
            0);

        if (_hook == IntPtr.Zero)
        {
            Log.Warn($"keyboard hook failed: error {Marshal.GetLastWin32Error()}");
        }
        else
        {
            Log.Info("keyboard hook installed");
        }
    }

    public void SetBindings(
        IEnumerable<SoundItem> items,
        IReadOnlyDictionary<string, string> controlHotkeys,
        bool multiSounds,
        GroupMode groupMode)
    {
        _multiSounds = multiSounds;
        _groupMode = groupMode;
        _bindings.Clear();

        foreach (var item in items)
        {
            if (!HotkeyBinding.TryParse(item.Hotkey, out var binding) || binding is null)
            {
                continue;
            }

            if (!_bindings.TryGetValue(binding.VirtualKey, out var list))
            {
                _bindings[binding.VirtualKey] = list = [];
            }

            if (!_multiSounds && list.Any(entry => entry.Binding == binding))
            {
                Log.Warn($"{item.Hotkey} is already taken; {item.Name} keeps the existing binding");
                continue;
            }

            list.Add(new Entry { Binding = binding, Sound = item });
        }

        foreach (var (id, text) in controlHotkeys)
        {
            if (!HotkeyBinding.TryParse(text, out var binding) || binding is null)
            {
                continue;
            }

            if (!_bindings.TryGetValue(binding.VirtualKey, out var list))
            {
                _bindings[binding.VirtualKey] = list = [];
            }

            if (list.Any(entry => entry.Binding == binding))
            {
                Log.Warn($"{text} is already taken; the {id} hotkey was not registered");
                continue;
            }

            list.Add(new Entry { Binding = binding, ControlId = id });
        }

        Log.Info($"hotkey bindings active: {_bindings.Count} keys, {controlHotkeys.Count} controls");
    }

    public void BeginCapture(SoundItem? sound, string? controlId = null)
    {
        _capturing = true;
        _captureSound = sound;
        _captureControl = controlId;
    }

    public void CancelCapture()
    {
        _capturing = false;
        _captureSound = null;
        _captureControl = null;
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var message = (int)wParam.ToInt64();
            var isDown = message is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
            var isUp = message is Native.WM_KEYUP or Native.WM_SYSKEYUP;

            if (isDown || isUp)
            {
                var data = Marshal.PtrToStructure<Native.KbdLlHookStruct>(lParam);
                var key = Normalise(data);
                var handled = isDown ? HandleDown(key) : HandleUp(key);
                if (handled)
                {
                    return 1;
                }
            }
        }

        return Native.CallNextHookEx(_hook, code, wParam, lParam);
    }

    private bool HandleDown(uint virtualKey)
    {
        if (_capturing)
        {
            if (virtualKey == 0x1B)
            {
                CancelCapture();
                CaptureCancelled?.Invoke();
                return true;
            }

            if (IsModifier(virtualKey))
            {
                // Wait for the real key; modifiers are part of the binding.
                return true;
            }

            var modifiers = HotkeyBinding.CurrentModifiers();
            var binding = HotkeyBinding.FromKey(
                virtualKey, modifiers.Ctrl, modifiers.Alt, modifiers.Shift, modifiers.Win);

            var sound = _captureSound;
            var control = _captureControl;
            CancelCapture();

            if (control is not null)
            {
                ControlCaptured?.Invoke(control, binding);
            }
            else if (sound is not null)
            {
                Captured?.Invoke(sound, binding);
            }

            return true;
        }

        if (!_bindings.TryGetValue(virtualKey, out var candidates))
        {
            return false;
        }

        var held = HotkeyBinding.CurrentModifiers();
        foreach (var entry in candidates)
        {
            if (entry.Binding.Ctrl != held.Ctrl ||
                entry.Binding.Alt != held.Alt ||
                entry.Binding.Shift != held.Shift ||
                entry.Binding.Win != held.Win)
            {
                continue;
            }

            if (_held.ContainsKey(virtualKey))
            {
                // Auto-repeat: this key is already down for its binding.
                return true;
            }

            if (entry.ControlId is string controlId)
            {
                _held[virtualKey] = entry;
                ControlTriggered?.Invoke(controlId, true);
                return true;
            }

            var sounds = candidates
                .Where(candidate => candidate.Sound is not null && candidate.Binding == entry.Binding)
                .Select(candidate => candidate.Sound!)
                .ToList();

            if (sounds.Count == 0)
            {
                return false;
            }

            var key = entry.Binding.Text;
            var chosen = Choose(sounds, key);
            _groupLast[key] = chosen.Id;

            var active = new Entry { Binding = entry.Binding, Sound = chosen };
            _held[virtualKey] = active;
            Triggered?.Invoke(chosen, true);
            return true;
        }

        return false;
    }

    private SoundItem Choose(List<SoundItem> sounds, string key)
    {
        if (sounds.Count == 1)
        {
            return sounds[0];
        }

        switch (_groupMode)
        {
            case GroupMode.Same:
                if (_groupLast.TryGetValue(key, out var last) &&
                    sounds.FirstOrDefault(sound => sound.Id == last) is { } same)
                {
                    return same;
                }

                return sounds[0];

            case GroupMode.Next:
                var index = _groupIndex.TryGetValue(key, out var next) ? next : 0;
                var picked = sounds[index % sounds.Count];
                _groupIndex[key] = (index + 1) % sounds.Count;
                return picked;

            default:
                return sounds[Random.Shared.Next(sounds.Count)];
        }
    }

    private bool HandleUp(uint virtualKey)
    {
        if (!_held.Remove(virtualKey, out var entry))
        {
            return false;
        }

        if (entry.ControlId is string controlId)
        {
            ControlTriggered?.Invoke(controlId, false);
        }
        else if (entry.Sound is { } sound)
        {
            Triggered?.Invoke(sound, false);
        }

        return true;
    }

    /// <summary>
    /// The key the user actually pressed. With NumLock off the numpad reports the
    /// navigation keys — numpad 7 arrives as Home — so a "Numpad7" binding would never
    /// match. The scan code plus the extended flag tell the two keyboards apart, which
    /// is the same distinction Windows itself uses.
    /// </summary>
    private static uint Normalise(Native.KbdLlHookStruct data)
    {
        const uint Extended = 0x01;
        const int NumLock = 0x90;

        if ((data.Flags & Extended) != 0 || (Native.GetKeyState(NumLock) & 1) != 0)
        {
            return data.VirtualKeyCode;
        }

        return data.ScanCode switch
        {
            0x47 => 0x67, // 7
            0x48 => 0x68, // 8
            0x49 => 0x69, // 9
            0x4A => 0x6D, // subtract
            0x4B => 0x64, // 4
            0x4C => 0x65, // 5
            0x4D => 0x66, // 6
            0x4E => 0x6B, // add
            0x4F => 0x61, // 1
            0x50 => 0x62, // 2
            0x51 => 0x63, // 3
            0x52 => 0x60, // 0
            0x53 => 0x6E, // decimal
            _ => data.VirtualKeyCode,
        };
    }

    private static bool IsModifier(uint virtualKey)
        => virtualKey is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
