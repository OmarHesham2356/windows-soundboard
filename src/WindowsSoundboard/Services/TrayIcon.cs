using System.Runtime.InteropServices;

namespace WindowsSoundboard.Services;

/// <summary>
/// Notification-area icon built directly on Shell_NotifyIcon, so the app keeps no
/// extra UI framework and no extra package for it. The icon owns a message-only
/// window on the UI thread; right-click opens a small native menu with show/hide,
/// mute and exit.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NimAdd = 0x00000000;
    private const uint NimDelete = 0x00000002;
    private const uint NimModify = 0x00000001;

    private const uint WmApp = 0x8000;
    private const uint WmLeftButtonUp = 0x0202;
    private const uint WmLeftButtonDoubleClick = 0x0203;
    private const uint WmRightButtonUp = 0x0205;

    private const uint MfString = 0x00000000;
    private const uint MfGrayed = 0x00000001;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCmd = 0x0100;

    private const int IdShowHide = 1;
    private const int IdMute = 2;
    private const int IdExit = 3;

    private static readonly IntPtr HwndMessage = new(-3);
    private static readonly uint CallbackMessage = WmApp + 1;

    private readonly WndProcDelegate _wndProc;
    private readonly Action _showHideRequested;
    private readonly Action _muteRequested;
    private readonly Action _exitRequested;
    private readonly Func<bool> _isWindowVisible;
    private readonly Func<bool> _isMuted;
    private readonly string _className = "WindowsSoundboardTray" + Guid.NewGuid().ToString("N");

    private IntPtr _hwnd;
    private IntPtr _icon;
    private string _toolTip;
    private bool _classRegistered;

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    public TrayIcon(
        string toolTip,
        Action showHideRequested,
        Action muteRequested,
        Action exitRequested,
        Func<bool> isWindowVisible,
        Func<bool> isMuted)
    {
        _toolTip = toolTip;
        _showHideRequested = showHideRequested;
        _muteRequested = muteRequested;
        _exitRequested = exitRequested;
        _isWindowVisible = isWindowVisible;
        _isMuted = isMuted;

        // The delegate must outlive the window: the OS keeps only a native pointer.
        _wndProc = WindowProc;

        var module = GetModuleHandleW(null);
        var windowClass = new WindowClassEx
        {
            Size = (uint)Marshal.SizeOf<WindowClassEx>(),
            Style = 0,
            WindowProc = _wndProc,
            ClsExtra = 0,
            WndExtra = 0,
            Instance = module,
            Icon = IntPtr.Zero,
            Cursor = IntPtr.Zero,
            Background = IntPtr.Zero,
            MenuName = null,
            ClassName = _className,
            IconSmall = IntPtr.Zero,
        };

        if (RegisterClassExW(ref windowClass) == 0)
        {
            throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastWin32Error()}");
        }

        _classRegistered = true;

        _hwnd = CreateWindowExW(
            0, _className, "Windows Soundboard", 0,
            0, 0, 0, 0, HwndMessage, IntPtr.Zero, module, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
        }

        _icon = LoadAppIcon();

        var data = CreateData(NifMessage | NifIcon | NifTip);
        if (!ShellNotifyIcon(NimAdd, ref data))
        {
            throw new InvalidOperationException($"Shell_NotifyIcon failed: {Marshal.GetLastWin32Error()}");
        }

        Log.Info("tray icon added");
    }

    public void SetToolTip(string toolTip)
    {
        _toolTip = toolTip;
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        var data = CreateData(NifTip);
        ShellNotifyIcon(NimModify, ref data);
    }

    private NotifyIconData CreateData(uint flags) => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        Window = _hwnd,
        Id = 1,
        Flags = flags,
        CallbackMessage = CallbackMessage,
        Icon = _icon,
        Tip = _toolTip,
    };

    private IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == CallbackMessage)
        {
            switch ((uint)lParam.ToInt64())
            {
                case WmLeftButtonUp:
                case WmLeftButtonDoubleClick:
                    _showHideRequested();
                    return IntPtr.Zero;
                case WmRightButtonUp:
                    ShowMenu();
                    return IntPtr.Zero;
            }
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            AppendMenuW(menu, MfString, IdShowHide, _isWindowVisible() ? "Hide window" : "Show window");
            AppendMenuW(menu, MfString, IdMute, _isMuted() ? "Unmute" : "Mute");
            AppendMenuW(menu, 0x00000800, 0, null); // separator
            AppendMenuW(menu, MfString, IdExit, "Exit");

            GetCursorPos(out var cursor);
            SetForegroundWindow(_hwnd);
            var command = TrackPopupMenu(menu, TpmRightButton | TpmReturnCmd, cursor.X, cursor.Y, 0, _hwnd, IntPtr.Zero);

            switch (command)
            {
                case IdShowHide:
                    _showHideRequested();
                    break;
                case IdMute:
                    _muteRequested();
                    break;
                case IdExit:
                    _exitRequested();
                    break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private static IntPtr LoadAppIcon()
    {
        var executable = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(executable))
        {
            try
            {
                var extracted = ExtractIconExW(executable, 0, out var large, out _, 1);
                if (extracted > 0 && large != IntPtr.Zero)
                {
                    return large;
                }
            }
            catch
            {
                // Fall through to the stock icon below.
            }
        }

        return LoadIconW(IntPtr.Zero, new IntPtr(32512)); // IDI_APPLICATION
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
        {
            var data = CreateData(0);
            ShellNotifyIcon(NimDelete, ref data);
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        if (_classRegistered)
        {
            UnregisterClassW(_className, GetModuleHandleW(null));
            _classRegistered = false;
        }

        Log.Info("tray icon removed");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr Icon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;

        public uint State;
        public uint StateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;

        public uint VersionOrTimeout;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;

        public uint InfoFlags;
        public Guid ItemGuid;
        public IntPtr BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        public uint Size;
        public uint Style;
        public WndProcDelegate WindowProc;
        public int ClsExtra;
        public int WndExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? MenuName;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string ClassName;

        public IntPtr IconSmall;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WindowClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClassW(string className, IntPtr instance);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr menu, uint flags, int id, string? text);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(string file, int index, out IntPtr large, out IntPtr small, uint count);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIconW(IntPtr instance, IntPtr iconName);
}
