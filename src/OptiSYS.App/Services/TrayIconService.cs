using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using OptiSYS.Models;

namespace OptiSYS.Services;

public interface ITrayIconService : IDisposable
{
    event Action? OpenRequested;
    event Action? RunCleanupRequested;
    event Action? ToggleAutomationRequested;
    event Action? ExitRequested;

    void Initialize(nint windowHandle);
    void Update(TraySnapshot snapshot);
}

public sealed class TrayIconService : ITrayIconService
{
    public event Action? OpenRequested;
    public event Action? RunCleanupRequested;
    public event Action? ToggleAutomationRequested;
    public event Action? ExitRequested;

    private Icon? _icon;
    private nint _iconHandle;
    private SubclassProc? _subclassProc;
    private nint _windowHandle;
    private bool _initialized;
    private TraySnapshot _snapshot = new();
    private TrayDot? _renderedDot;
    private string _lastTooltip = string.Empty;

    public void Initialize(nint windowHandle)
    {
        if (_initialized || windowHandle == nint.Zero)
        {
            return;
        }

        try
        {
            _windowHandle = windowHandle;
            RenderDotIcon(TrayHealthEvaluator.DotFor(_snapshot.HealthState));

            // Subclass the window so it receives the WM_TRAYICON callbacks (left-click = open,
            // right-click = context menu). Same subclass id used by Dispose's RemoveWindowSubclass.
            _subclassProc = WindowSubclassProc;
            SetWindowSubclass(windowHandle, _subclassProc, 1001, nint.Zero);

            // Add the notification-area icon and opt into modern (v4) behaviour.
            _lastTooltip = ClampTooltip(_snapshot.Tooltip);
            var data = CreateNotifyIconData(_iconHandle, _lastTooltip);
            TryNotifyIcon(NIM_ADD, ref data, "add");
            TryNotifyIcon(NIM_SETVERSION, ref data, "setversion");

            _initialized = true;
            StartupLog.Write("TrayIconService: initialized");
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("TrayIconService.Initialize failure", ex);
        }
    }

    public void Update(TraySnapshot snapshot)
    {
        if (!_initialized)
        {
            return;
        }

        _snapshot = snapshot;

        // The dot colour is theme-independent; re-render only when the health state's dot changes.
        var dot = TrayHealthEvaluator.DotFor(snapshot.HealthState);
        var iconChanged = _renderedDot != dot;
        if (iconChanged)
        {
            RenderDotIcon(dot);
        }

        var tooltip = ClampTooltip(snapshot.Tooltip);
        var tooltipChanged = !string.Equals(tooltip, _lastTooltip, StringComparison.Ordinal);

        // Skip the shell round-trip entirely when nothing the user sees has changed.
        if (!iconChanged && !tooltipChanged)
        {
            return;
        }

        _lastTooltip = tooltip;
        var data = CreateNotifyIconData(_iconHandle, tooltip);
        TryNotifyIcon(NIM_MODIFY, ref data, "modify");
    }

    public void Dispose()
    {
        if (_initialized)
        {
            var data = CreateNotifyIconData(_iconHandle, "optiSYS");
            TryNotifyIcon(NIM_DELETE, ref data, "delete");
        }

        if (_windowHandle != nint.Zero && _subclassProc != null)
        {
            RemoveWindowSubclass(_windowHandle, _subclassProc, 1001);
            _subclassProc = null;
        }

        _windowHandle = nint.Zero;
        DisposeIcon();
        _initialized = false;
    }

    private nint WindowSubclassProc(nint hwnd, uint msg, nuint wParam, nint lParam, nuint uIdSubclass, nint dwRefData)
    {
        if (msg == WM_TRAYICON)
        {
            var eventId = ExtractTrayEventId(lParam);
            switch (eventId)
            {
                case WM_LBUTTONUP:
                case NIN_SELECT:
                case NIN_KEYSELECT:
                    OpenRequested?.Invoke();
                    break;
                case WM_RBUTTONUP:
                case WM_CONTEXTMENU:
                    ShowContextMenu();
                    break;
            }
        }

        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    private static string ClampTooltip(string tooltip)
    {
        if (string.IsNullOrWhiteSpace(tooltip))
        {
            return "optiSYS";
        }

        const int maxLength = 63;
        return tooltip.Length <= maxLength
            ? tooltip
            : tooltip[..maxLength];
    }

    internal static uint ExtractTrayEventId(nint lParam) =>
        unchecked((uint)(lParam.ToInt64() & 0xFFFF));

    internal static Color SelectStrokeColor(bool isLightTheme) =>
        isLightTheme
            ? Color.FromArgb(29, 33, 36)
            : Color.FromArgb(238, 244, 239);

    private static bool TryNotifyIcon(uint message, ref NOTIFYICONDATA data, string action)
    {
        if (Shell_NotifyIcon(message, ref data))
        {
            return true;
        }

        StartupLog.Write($"TrayIconService: Shell_NotifyIcon {action} failed win32={Marshal.GetLastWin32Error()}");
        return false;
    }

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        try
        {
            AppendMenu(menu, MF_STRING, ID_CLEANUP, "Optimize now");
            AppendMenu(
                menu,
                MF_STRING,
                ID_TOGGLE_AUTOMATION,
                _snapshot.AutomationPaused ? "Auto-Optimize: Off" : "Auto-Optimize: On");
            AppendMenu(menu, MF_STRING, ID_OPEN, "Show optiSYS");
            AppendMenu(menu, MF_SEPARATOR, 0, string.Empty);
            AppendMenu(menu, MF_STRING, ID_EXIT, "Exit");

            SetForegroundWindow(_windowHandle);
            GetCursorPos(out var cursor);
            var command = TrackPopupMenuEx(
                menu,
                TPM_RETURNCMD | TPM_BOTTOMALIGN | TPM_LEFTALIGN | TPM_RIGHTBUTTON,
                cursor.X,
                cursor.Y,
                _windowHandle,
                nint.Zero);

            PostMessage(_windowHandle, WM_NULL, 0, 0);

            switch ((uint)command)
            {
                case ID_OPEN:
                    OpenRequested?.Invoke();
                    break;
                case ID_CLEANUP:
                    RunCleanupRequested?.Invoke();
                    break;
                case ID_TOGGLE_AUTOMATION:
                    ToggleAutomationRequested?.Invoke();
                    break;
                case ID_EXIT:
                    ExitRequested?.Invoke();
                    break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    /// <summary>
    /// Renders the health dot into a fresh 32x32 transparent icon and swaps it in, freeing the
    /// previous GDI handle.
    /// </summary>
    private void RenderDotIcon(TrayDot dot)
    {
        DisposeIcon();
        _icon = DotIconRenderer.Render(dot, out _iconHandle);
        _renderedDot = dot;
    }

    private void DisposeIcon()
    {
        _icon?.Dispose();
        _icon = null;
        if (_iconHandle != nint.Zero)
        {
            DestroyIcon(_iconHandle);
            _iconHandle = nint.Zero;
        }
    }

    private NOTIFYICONDATA CreateNotifyIconData(nint iconHandle, string tooltip) =>
        new()
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _windowHandle,
            uID = TRAY_ICON_ID,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = iconHandle,
            szTip = ClampTooltip(tooltip),
            szInfo = string.Empty,
            uVersion = NOTIFYICON_VERSION_4,
            szInfoTitle = string.Empty,
        };

    /// <summary>
    /// Renders the 3-state health dot as a 32x32 transparent icon (filled circle). Returns a managed
    /// <see cref="Icon"/> clone that owns its data (safe to Dispose); <paramref name="handle"/> is
    /// the raw HICON the caller must DestroyIcon.
    /// </summary>
    internal static class DotIconRenderer
    {
        private static readonly Color Green = Color.FromArgb(0x2E, 0xB8, 0x4C);
        private static readonly Color Yellow = Color.FromArgb(0xE0, 0xA8, 0x16);
        private static readonly Color Red = Color.FromArgb(0xE0, 0x43, 0x43);

        public static Icon Render(TrayDot dot, out nint handle)
        {
            const int size = 32;
            using var bmp = new Bitmap(size, size);
            using var g = Graphics.FromImage(bmp);

            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var color = dot switch
            {
                TrayDot.Green => Green,
                TrayDot.Yellow => Yellow,
                _ => Red,
            };
            using var brush = new SolidBrush(color);

            const int pad = 6; // ~20px dot centred in the 32px canvas
            g.FillEllipse(brush, pad, pad, size - 2 * pad, size - 2 * pad);

            // Clone creates a managed Icon that owns its data; the raw HICON is returned so the
            // caller can DestroyIcon it once the clone is in use, preventing GDI handle leaks.
            handle = bmp.GetHicon();
            return (Icon)Icon.FromHandle(handle).Clone();
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;

        public uint uVersion
        {
            get => uTimeoutOrVersion;
            set => uTimeoutOrVersion = value;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProc(nint hWnd, uint uMsg, nuint wParam, nint lParam, nuint uIdSubclass, nint dwRefData);

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIM_SETVERSION = 0x00000004;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint NIF_SHOWTIP = 0x00000080;
    private const uint NOTIFYICON_VERSION_4 = 4;
    private const uint WM_APP = 0x8000;
    private const uint WM_NULL = 0x0000;
    private const uint WM_USER = 0x0400;
    private const uint WM_CONTEXTMENU = 0x007B;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_TRAYICON = WM_APP + 1;
    private const uint NIN_SELECT = WM_USER;
    private const uint NIN_KEYSELECT = WM_USER + 1;
    private const int GWLP_WNDPROC = -4;
    private const uint MF_STRING = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint TPM_LEFTALIGN = 0x0000;
    private const uint TPM_BOTTOMALIGN = 0x0020;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TRAY_ICON_ID = 1;
    private const uint ID_OPEN = 101;
    private const uint ID_CLEANUP = 102;
    private const uint ID_TOGGLE_AUTOMATION = 103;
    private const uint ID_EXIT = 105;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(nint hWnd, SubclassProc pfnSubclass, nuint uIdSubclass, nint dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(nint hWnd, SubclassProc pfnSubclass, nuint uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hWnd, uint uMsg, nuint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool AppendMenu(nint hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyMenu(nint hMenu);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int TrackPopupMenuEx(
        nint hmenu,
        uint fuFlags,
        int x,
        int y,
        nint hwnd,
        nint lptpm);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(nint hWnd, uint msg, nuint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(nint hIcon);
}
