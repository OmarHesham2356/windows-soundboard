using System.Text;

namespace WindowsSoundboard.Models;

/// <summary>
/// A parsed key binding such as "Ctrl+Shift+F1", "Numpad1" or "F9".
/// Keys are matched exactly: a plain "F9" binding does not fire while Ctrl is held.
/// </summary>
public sealed record HotkeyBinding(uint VirtualKey, bool Ctrl, bool Alt, bool Shift, bool Win)
{
    private static readonly Dictionary<string, uint> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 0x20,
        ["Enter"] = 0x0D,
        ["Tab"] = 0x09,
        ["Backspace"] = 0x08,
        ["Escape"] = 0x1B,
        ["Insert"] = 0x2D,
        ["Delete"] = 0x2E,
        ["Home"] = 0x24,
        ["End"] = 0x23,
        ["PageUp"] = 0x21,
        ["PageDown"] = 0x22,
        ["Up"] = 0x26,
        ["Down"] = 0x28,
        ["Left"] = 0x25,
        ["Right"] = 0x27,
        ["NumpadAdd"] = 0x6B,
        ["NumpadSubtract"] = 0x6D,
        ["NumpadMultiply"] = 0x6A,
        ["NumpadDivide"] = 0x6F,
        ["NumpadDecimal"] = 0x6E,
        ["Semicolon"] = 0xBA,
        ["Equals"] = 0xBB,
        ["Comma"] = 0xBC,
        ["Minus"] = 0xBD,
        ["Period"] = 0xBE,
        ["Slash"] = 0xBF,
        ["Backtick"] = 0xC0,
        ["BracketLeft"] = 0xDB,
        ["Backslash"] = 0xDC,
        ["BracketRight"] = 0xDD,
        ["Quote"] = 0xDE,
        ["CapsLock"] = 0x14,
        ["ScrollLock"] = 0x91,
        ["Pause"] = 0x13,
        ["PrintScreen"] = 0x2C,
    };

    public string Text
    {
        get
        {
            var builder = new StringBuilder();
            if (Ctrl)
            {
                builder.Append("Ctrl+");
            }

            if (Alt)
            {
                builder.Append("Alt+");
            }

            if (Shift)
            {
                builder.Append("Shift+");
            }

            if (Win)
            {
                builder.Append("Win+");
            }

            builder.Append(Describe(VirtualKey));
            return builder.ToString();
        }
    }

    public static HotkeyBinding FromKey(uint virtualKey, bool ctrl, bool alt, bool shift, bool win)
        => new(virtualKey, ctrl, alt, shift, win);

    public static bool TryParse(string? text, out HotkeyBinding? binding)
    {
        binding = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var ctrl = false;
        var alt = false;
        var shift = false;
        var win = false;
        uint? key = null;

        foreach (var rawToken in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = rawToken.Trim();
            if (token.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                ctrl = true;
            }
            else if (token.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                alt = true;
            }
            else if (token.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                shift = true;
            }
            else if (token.Equals("Win", StringComparison.OrdinalIgnoreCase))
            {
                win = true;
            }
            else if (TryParseKey(token, out var parsed))
            {
                key = parsed;
            }
            else
            {
                return false;
            }
        }

        if (key is null)
        {
            return false;
        }

        binding = new HotkeyBinding(key.Value, ctrl, alt, shift, win);
        return true;
    }

    public static string Describe(uint virtualKey)
    {
        foreach (var pair in NamedKeys)
        {
            if (pair.Value == virtualKey)
            {
                return pair.Key;
            }
        }

        if (virtualKey is >= 0x41 and <= 0x5A)
        {
            return ((char)virtualKey).ToString();
        }

        if (virtualKey is >= 0x30 and <= 0x39)
        {
            return ((char)virtualKey).ToString();
        }

        if (virtualKey is >= 0x60 and <= 0x69)
        {
            return "Numpad" + (virtualKey - 0x60);
        }

        if (virtualKey is >= 0x70 and <= 0x87)
        {
            return "F" + (virtualKey - 0x6F);
        }

        return "0x" + virtualKey.ToString("X2");
    }

    private static bool TryParseKey(string token, out uint virtualKey)
    {
        virtualKey = 0;

        if (token.Length == 1)
        {
            var character = char.ToUpperInvariant(token[0]);
            if (character is >= 'A' and <= 'Z')
            {
                virtualKey = character;
                return true;
            }

            if (character is >= '0' and <= '9')
            {
                virtualKey = character;
                return true;
            }
        }

        if (token.StartsWith('F') &&
            int.TryParse(token.AsSpan(1), out var functionNumber) &&
            functionNumber is >= 1 and <= 24)
        {
            virtualKey = (uint)(0x6F + functionNumber);
            return true;
        }

        if (token.StartsWith("Numpad", StringComparison.OrdinalIgnoreCase) &&
            token.Length == 7 &&
            char.IsDigit(token[6]))
        {
            virtualKey = (uint)(0x60 + (token[6] - '0'));
            return true;
        }

        return NamedKeys.TryGetValue(token, out virtualKey);
    }

    /// <summary>Modifiers held right now, used to describe captured keys.</summary>
    public static (bool Ctrl, bool Alt, bool Shift, bool Win) CurrentModifiers()
    {
        static bool IsDown(int vk) => (Native.GetAsyncKeyState(vk) & 0x8000) != 0;
        return (IsDown(0x11) || IsDown(0xA2) || IsDown(0xA3),
                IsDown(0x12) || IsDown(0xA4) || IsDown(0xA5),
                IsDown(0x10) || IsDown(0xA0) || IsDown(0xA1),
                IsDown(0x5B) || IsDown(0x5C));
    }
}
