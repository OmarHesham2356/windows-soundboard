using System.Runtime.InteropServices;
using WindowsSoundboard.Models;

namespace WindowsSoundboard.Services;

/// <summary>
/// A global low-level keyboard hook. It runs on the UI thread's message loop, so
/// events arrive on the UI thread. Bound keys are swallowed, so pressing a pad key
/// does not also type into whatever application has focus.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private readonly Native.LowLevelKeyboardProc _callback;
    private readonly Dictionary<uint, List<(HotkeyBinding Binding, SoundItem Item)>> _bindings = [];
    private readonly Dictionary<uint, SoundItem> _held = [];

    private IntPtr _hook;
    private bool _capturing;
    private SoundItem? _captureTarget;

    public HotkeyService()
    {
        // Keep the delegate in a field: the OS holds only a native pointer to it.
        _callback = HookCallback;
    }

    /// <summary>Fires on key down (true) and key up (false) for a bound sound.</summary>
    public event Action<SoundItem, bool>? Triggered;

    /// <summary>Fires when a key was captured for a sound that was waiting for one.</summary>
    public event Action<SoundItem, HotkeyBinding>? Captured;

    /// <summary>Fires when capture was abandoned with Escape.</summary>
    public event Action? CaptureCancelled;

    public bool IsRunning => _hook != IntPtr.Zero;

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

    public void SetBindings(IEnumerable<SoundItem> items)
    {
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

            list.Add((binding, item));
        }

        Log.Info($"hotkey bindings active: {_bindings.Count} keys");
    }

    public void BeginCapture(SoundItem item)
    {
        _capturing = true;
        _captureTarget = item;
    }

    public void CancelCapture()
    {
        _capturing = false;
        _captureTarget = null;
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
                var handled = isDown ? HandleDown(data.VirtualKeyCode) : HandleUp(data.VirtualKeyCode);
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

            var target = _captureTarget;
            _capturing = false;
            _captureTarget = null;

            if (target is not null)
            {
                Captured?.Invoke(target, binding);
            }

            return true;
        }

        if (!_bindings.TryGetValue(virtualKey, out var candidates))
        {
            return false;
        }

        var held = HotkeyBinding.CurrentModifiers();
        foreach (var (binding, item) in candidates)
        {
            if (binding.Ctrl != held.Ctrl ||
                binding.Alt != held.Alt ||
                binding.Shift != held.Shift ||
                binding.Win != held.Win)
            {
                continue;
            }

            if (!_held.TryAdd(virtualKey, item))
            {
                // Auto-repeat: this key is already down for its binding.
                return true;
            }

            Triggered?.Invoke(item, true);
            return true;
        }

        return false;
    }

    private bool HandleUp(uint virtualKey)
    {
        if (!_held.Remove(virtualKey, out var item))
        {
            return false;
        }

        Triggered?.Invoke(item, false);
        return true;
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
