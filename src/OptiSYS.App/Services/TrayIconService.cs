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

    private TrayIconSet? _icons;
    private SubclassProc? _subclassProc;
    private nint _windowHandle;
    private bool _initialized;
    private TraySnapshot _snapshot = new();
    private IconTheme _iconTheme;

    public void Initialize(nint windowHandle)
    {
        StartupLog.Write("TrayIconService: initialization bypassed for diagnostics");
        _initialized = true;
    }

    public void Update(TraySnapshot snapshot)
    {
        if (_icons is null || !_initialized)
        {
            return;
        }

        RefreshIconSetIfThemeChanged();
        _snapshot = snapshot;
        var data = CreateNotifyIconData(_icons.For(snapshot.HealthState), ClampTooltip(snapshot.Tooltip));
        TryNotifyIcon(NIM_MODIFY, ref data, "modify");
    }

    public void Dispose()
    {
        if (_initialized)
        {
            var iconHandle = _icons is not null
                ? _icons.For(OverallHealthState.Normal).Handle
                : nint.Zero;
            var data = CreateNotifyIconData(iconHandle, "optiSYS");
            TryNotifyIcon(NIM_DELETE, ref data, "delete");
        }

        if (_windowHandle != nint.Zero && _subclassProc != null)
        {
            RemoveWindowSubclass(_windowHandle, _subclassProc, 1001);
            _subclassProc = null;
        }

        _windowHandle = nint.Zero;
        _icons?.Dispose();
        _icons = null;
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
            AppendMenu(menu, MF_STRING, ID_OPEN, "Open optiSYS");
            AppendMenu(menu, MF_STRING, ID_CLEANUP, "Run safe cleanup now");
            AppendMenu(
                menu,
                MF_STRING,
                ID_TOGGLE_AUTOMATION,
                _snapshot.AutomationPaused ? "Resume safe optimization" : "Pause safe optimization");
            AppendMenu(menu, MF_DISABLED, ID_MODE, "Mode: safe runtime only");
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

    private void RefreshIconSetIfThemeChanged()
    {
        var detected = IconTheme.Detect();
        if (detected == _iconTheme)
        {
            return;
        }

        _iconTheme = detected;
        var previousIcons = _icons;
        _icons = TrayIconSet.Create(_iconTheme);
        previousIcons?.Dispose();
    }

    private NOTIFYICONDATA CreateNotifyIconData(Icon icon, string tooltip) =>
        CreateNotifyIconData(icon.Handle, tooltip);

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

    private sealed class TrayIconSet : IDisposable
    {
        private readonly Dictionary<OverallHealthState, Icon> _icons;
        private readonly List<nint> _handles;

        private TrayIconSet(Dictionary<OverallHealthState, Icon> icons, List<nint> handles)
        {
            _icons = icons;
            _handles = handles;
        }

        public Icon For(OverallHealthState state) => _icons[state];

        public static TrayIconSet Create(IconTheme theme)
        {
            var colors = new Dictionary<OverallHealthState, Color>
            {
                [OverallHealthState.Bad] = Color.FromArgb(202, 89, 72),
                [OverallHealthState.NotGood] = Color.FromArgb(223, 149, 72),
                [OverallHealthState.Normal] = theme.AccentColor,
                [OverallHealthState.Good] = theme.AccentColor,
                [OverallHealthState.Great] = theme.AccentColor,
            };

            var icons = new Dictionary<OverallHealthState, Icon>();
            var handles = new List<nint>();
            foreach (var (state, accent) in colors)
            {
                icons[state] = CreatePulseIcon(accent, theme.StrokeColor, handles);
            }

            return new TrayIconSet(icons, handles);
        }

        public void Dispose()
        {
            foreach (var icon in _icons.Values)
            {
                icon.Dispose();
            }

            foreach (var handle in _handles)
            {
                if (handle != nint.Zero)
                {
                    DestroyIcon(handle);
                }
            }

            _icons.Clear();
            _handles.Clear();
        }

        private static Icon CreatePulseIcon(Color accent, Color stroke, List<nint> handles)
        {
            using var bitmap = new Bitmap(32, 32);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

            using var strokePen = new Pen(stroke, 4f)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round,
                LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
            };
            using var accentBrush = new SolidBrush(accent);

            var points = new[]
            {
                new PointF(4, 18),
                new PointF(10, 18),
                new PointF(14, 10),
                new PointF(18, 23),
                new PointF(23, 15),
                new PointF(28, 15),
            };
            graphics.DrawLines(strokePen, points);
            graphics.FillEllipse(accentBrush, 15.5f, 20.5f, 7f, 7f);

            var handle = bitmap.GetHicon();
            handles.Add(handle);
            return (Icon)Icon.FromHandle(handle).Clone();
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(nint hIcon);
    }

    private readonly record struct IconTheme(Color AccentColor, Color StrokeColor)
    {
        public static IconTheme Detect()
        {
            var accent = TryGetDwmAccentColor(out var dwmAccent)
                ? dwmAccent
                : Color.FromArgb(76, 159, 85);
            var stroke = SelectStrokeColor(IsWindowsLightTheme());
            return new IconTheme(accent, stroke);
        }

        private static bool IsWindowsLightTheme()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("SystemUsesLightTheme") is int value
                    ? value != 0
                    : key?.GetValue("AppsUseLightTheme") is int appsValue && appsValue != 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetDwmAccentColor(out Color accent)
        {
            accent = default;
            try
            {
                if (DwmGetColorizationColor(out var colorization, out _) != 0)
                {
                    return false;
                }

                accent = Color.FromArgb(
                    255,
                    (int)((colorization >> 16) & 0xFF),
                    (int)((colorization >> 8) & 0xFF),
                    (int)(colorization & 0xFF));
                return true;
            }
            catch
            {
                return false;
            }
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
    private const uint MF_DISABLED = 0x00000002;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint TPM_LEFTALIGN = 0x0000;
    private const uint TPM_BOTTOMALIGN = 0x0020;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TRAY_ICON_ID = 1;
    private const uint ID_OPEN = 101;
    private const uint ID_CLEANUP = 102;
    private const uint ID_TOGGLE_AUTOMATION = 103;
    private const uint ID_MODE = 104;
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

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmGetColorizationColor(out uint pcrColorization, out bool pfOpaqueBlend);
}
