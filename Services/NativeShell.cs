using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace EverySearch;

/// <summary>
/// The Win32 bits WinUI doesn't provide: a global hotkey and a notification-area icon. Both deliver their messages
/// to the window's HWND, which is subclassed to catch them.
/// </summary>
internal sealed class NativeShell : IDisposable
{
    public const int MenuShow = 1, MenuRescan = 2, MenuQuit = 3;

    private const int HotkeyId = 1;
    private const uint WmHotkey = 0x0312, WmTray = 0x8001, WmLButtonUp = 0x0202, WmRButtonUp = 0x0205;
    private const uint ModAlt = 1, ModControl = 2, ModShift = 4, ModWin = 8, ModNoRepeat = 0x4000;

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);

    private readonly IntPtr hwnd;
    private readonly SubclassProc proc; // kept in a field so the GC can't collect the native callback
    private readonly uint taskbarCreated;
    private string? trayIcon, trayTip;
    private bool hotkeyRegistered, trayAdded;

    public event Action? HotkeyPressed;
    public event Action? TrayClicked;
    public event Action<int>? TrayMenuChosen;

    public NativeShell(IntPtr hwnd)
    {
        this.hwnd = hwnd;
        taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        proc = WndProc;
        SetWindowSubclass(hwnd, proc, (UIntPtr)1, UIntPtr.Zero);
    }

    // ---- hotkey ------------------------------------------------------------------------------------------------

    /// <summary>Parses "Ctrl+Alt+Space" style text. Needs at least one modifier and a key.</summary>
    public static bool TryParseHotkey(string spec, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        foreach (var raw in spec.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= ModControl; continue;
                case "alt": mods |= ModAlt; continue;
                case "shift": mods |= ModShift; continue;
                case "win" or "windows": mods |= ModWin; continue;
            }
            string name = raw.Length == 1 && char.IsDigit(raw[0]) ? "Number" + raw : raw;
            if (!Enum.TryParse<VirtualKey>(name, true, out var key)) return false;
            vk = (uint)key;
        }
        return mods != 0 && vk != 0;
    }

    public static string FormatHotkey(bool ctrl, bool alt, bool shift, bool win, VirtualKey key)
    {
        var parts = new List<string>();
        if (ctrl) parts.Add("Ctrl");
        if (alt) parts.Add("Alt");
        if (shift) parts.Add("Shift");
        if (win) parts.Add("Win");
        parts.Add(key.ToString().Replace("Number", ""));
        return string.Join('+', parts);
    }

    public bool RegisterHotkey(string spec, out string? error)
    {
        error = null;
        if (hotkeyRegistered) { UnregisterHotKey(hwnd, HotkeyId); hotkeyRegistered = false; }
        if (!TryParseHotkey(spec, out var mods, out var vk)) { error = $"\"{spec}\" isn't a valid shortcut"; return false; }
        hotkeyRegistered = RegisterHotKey(hwnd, HotkeyId, mods | ModNoRepeat, vk);
        if (!hotkeyRegistered) error = $"{spec} is already used by another app";
        return hotkeyRegistered;
    }

    // ---- tray icon ---------------------------------------------------------------------------------------------

    public void AddTrayIcon(string iconPath, string tip)
    {
        trayIcon = iconPath;
        trayTip = tip;
        var data = NewIconData();
        trayAdded = Shell_NotifyIcon(0 /* NIM_ADD */, ref data);
    }

    private NOTIFYICONDATA NewIconData()
    {
        IntPtr icon = trayIcon != null && File.Exists(trayIcon)
            ? LoadImage(IntPtr.Zero, trayIcon, 1 /* IMAGE_ICON */, 16, 16, 0x10 /* LR_LOADFROMFILE */)
            : IntPtr.Zero;
        return new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = hwnd,
            uID = 1,
            uFlags = 0x1 | 0x2 | 0x4, // NIF_MESSAGE | NIF_ICON | NIF_TIP
            uCallbackMessage = WmTray,
            hIcon = icon,
            szTip = trayTip ?? "",
            szInfo = "",
            szInfoTitle = "",
        };
    }

    private void ShowTrayMenu()
    {
        IntPtr menu = CreatePopupMenu();
        AppendMenu(menu, 0, MenuShow, "Open EverySearch");
        AppendMenu(menu, 0, MenuRescan, "Rescan index");
        AppendMenu(menu, 0x800 /* MF_SEPARATOR */, 0, null);
        AppendMenu(menu, 0, MenuQuit, "Quit");
        GetCursorPos(out var pt);
        SetForegroundWindow(hwnd); // required, or the menu won't close when you click elsewhere
        int cmd = TrackPopupMenu(menu, 0x100 | 0x2 | 0x20 /* RETURNCMD | RIGHTBUTTON | BOTTOMALIGN */, pt.X, pt.Y, 0, hwnd, IntPtr.Zero);
        PostMessage(hwnd, 0, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(menu);
        if (cmd != 0) TrayMenuChosen?.Invoke(cmd);
    }

    public static (int X, int Y) CursorPosition()
    {
        GetCursorPos(out var pt);
        return (pt.X, pt.Y);
    }

    public static void BringToFront(IntPtr hwnd) => SetForegroundWindow(hwnd);

    // ---- window procedure --------------------------------------------------------------------------------------

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
    {
        if (msg == WmHotkey && (int)wParam == HotkeyId)
        {
            HotkeyPressed?.Invoke();
            return IntPtr.Zero;
        }
        if (msg == WmTray)
        {
            uint mouse = (uint)((long)lParam & 0xFFFF);
            if (mouse == WmLButtonUp) TrayClicked?.Invoke();
            else if (mouse == WmRButtonUp) ShowTrayMenu();
            return IntPtr.Zero;
        }
        if (msg == taskbarCreated && trayIcon != null) AddTrayIcon(trayIcon, trayTip ?? ""); // explorer restarted
        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (hotkeyRegistered) UnregisterHotKey(hwnd, HotkeyId);
        if (trayAdded)
        {
            var data = NewIconData();
            Shell_NotifyIcon(2 /* NIM_DELETE */, ref data);
        }
        RemoveWindowSubclass(hwnd, proc, (UIntPtr)1);
    }

    // ---- P/Invoke ----------------------------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc proc, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc proc, UIntPtr id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint load);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, nuint id, string? text);
    [DllImport("user32.dll")] private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hWnd, IntPtr rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);
}
