 //! Minimal Windows `user32` interop scaffold for the optiSYS Zig core.
 //!
 //! This module intentionally exposes only a small subset of `user32` needed
 //! for backend-oriented monitoring and foreground-awareness decisions.
 //!
 //! Current goals:
 //! - identify the foreground window and owning process
 //! - support lightweight visibility/activation heuristics
 //! - avoid any UI framework dependency
 //!
 //! This is a scaffold, not a full wrapper surface.

 const std = @import("std");

 pub const BOOL = i32;
 pub const BYTE = u8;
 pub const WORD = u16;
 pub const DWORD = u32;
 pub const UINT = u32;
 pub const LONG = i32;
 pub const ULONG_PTR = usize;
 pub const LPARAM = isize;
 pub const WPARAM = usize;
 pub const LRESULT = isize;
 pub const HANDLE = ?*anyopaque;
 pub const HWND = ?*anyopaque;
 pub const HMONITOR = ?*anyopaque;
 pub const HINSTANCE = ?*anyopaque;
 pub const HMENU = ?*anyopaque;
 pub const HCURSOR = ?*anyopaque;
 pub const HICON = ?*anyopaque;
 pub const HBRUSH = ?*anyopaque;
 pub const LPVOID = ?*anyopaque;
 pub const LPCVOID = ?*const anyopaque;
 pub const WCHAR = u16;
 pub const LPCWSTR = ?[*:0]const WCHAR;
 pub const LPWSTR = ?[*:0]WCHAR;

 pub const FALSE: BOOL = 0;
 pub const TRUE: BOOL = 1;

 pub const SW_HIDE: c_int = 0;
 pub const SW_SHOWNORMAL: c_int = 1;
 pub const SW_SHOWMINIMIZED: c_int = 2;
 pub const SW_SHOWMAXIMIZED: c_int = 3;
 pub const SW_SHOWNOACTIVATE: c_int = 4;
 pub const SW_SHOW: c_int = 5;
 pub const SW_MINIMIZE: c_int = 6;
 pub const SW_SHOWMINNOACTIVE: c_int = 7;
 pub const SW_SHOWNA: c_int = 8;
 pub const SW_RESTORE: c_int = 9;
 pub const SW_SHOWDEFAULT: c_int = 10;

 pub const GWL_STYLE: c_int = -16;
 pub const GWL_EXSTYLE: c_int = -20;

 pub const WS_VISIBLE: LONG = 0x10000000;
 pub const WS_MINIMIZE: LONG = 0x20000000;
 pub const WS_DISABLED: LONG = 0x08000000;

 pub const MONITOR_DEFAULTTONULL: DWORD = 0x00000000;
 pub const MONITOR_DEFAULTTOPRIMARY: DWORD = 0x00000001;
 pub const MONITOR_DEFAULTTONEAREST: DWORD = 0x00000002;

 pub const Error = error{
     InvalidWindow,
     AccessDenied,
     InvalidParameter,
     Win32Failure,
     Unexpected,
 };

 pub const POINT = extern struct {
     x: LONG,
     y: LONG,
 };

 pub const RECT = extern struct {
     left: LONG,
     top: LONG,
     right: LONG,
     bottom: LONG,

     pub fn width(self: RECT) LONG {
         return self.right - self.left;
     }

     pub fn height(self: RECT) LONG {
         return self.bottom - self.top;
     }

     pub fn isEmpty(self: RECT) bool {
         return self.width() <= 0 or self.height() <= 0;
     }
 };

 pub const WINDOWPLACEMENT = extern struct {
     length: UINT,
     flags: UINT,
     showCmd: UINT,
     ptMinPosition: POINT,
     ptMaxPosition: POINT,
     rcNormalPosition: RECT,
 };

 pub const MONITORINFO = extern struct {
     cbSize: DWORD,
     rcMonitor: RECT,
     rcWork: RECT,
     dwFlags: DWORD,
 };

 pub extern "user32" fn GetForegroundWindow() callconv(.winapi) HWND;
 pub extern "user32" fn GetWindowThreadProcessId(
     hWnd: HWND,
     lpdwProcessId: *DWORD,
 ) callconv(.winapi) DWORD;
 pub extern "user32" fn IsWindow(hWnd: HWND) callconv(.winapi) BOOL;
 pub extern "user32" fn IsWindowVisible(hWnd: HWND) callconv(.winapi) BOOL;
 pub extern "user32" fn IsIconic(hWnd: HWND) callconv(.winapi) BOOL;
 pub extern "user32" fn GetWindowRect(
     hWnd: HWND,
     lpRect: *RECT,
 ) callconv(.winapi) BOOL;
 pub extern "user32" fn GetWindowPlacement(
     hWnd: HWND,
     lpwndpl: *WINDOWPLACEMENT,
 ) callconv(.winapi) BOOL;
 pub extern "user32" fn ShowWindow(
     hWnd: HWND,
     nCmdShow: c_int,
 ) callconv(.winapi) BOOL;
 pub extern "user32" fn SetForegroundWindow(
     hWnd: HWND,
 ) callconv(.winapi) BOOL;
 pub extern "user32" fn FindWindowW(
     lpClassName: LPCWSTR,
     lpWindowName: LPCWSTR,
 ) callconv(.winapi) HWND;
 pub extern "user32" fn MonitorFromWindow(
     hwnd: HWND,
     dwFlags: DWORD,
 ) callconv(.winapi) HMONITOR;
 pub extern "user32" fn GetMonitorInfoW(
     hMonitor: HMONITOR,
     lpmi: *MONITORINFO,
 ) callconv(.winapi) BOOL;
 pub extern "user32" fn GetWindowLongW(
     hWnd: HWND,
     nIndex: c_int,
 ) callconv(.winapi) LONG;

 extern "kernel32" fn GetLastError() callconv(.winapi) DWORD;

 pub const ForegroundWindowInfo = struct {
     hwnd: HWND,
     process_id: DWORD,
     thread_id: DWORD,
     visible: bool,
     minimized: bool,
     rect: RECT,

     pub fn hasUsableBounds(self: ForegroundWindowInfo) bool {
         return !self.rect.isEmpty();
     }
 };

 pub fn succeeded(value: BOOL) bool {
     return value != FALSE;
 }

 pub fn failed(value: BOOL) bool {
     return value == FALSE;
 }

 pub fn lastError() DWORD {
     return GetLastError();
 }

 pub fn mapLastError(err: DWORD) Error {
     return switch (err) {
         5 => error.AccessDenied, // ERROR_ACCESS_DENIED
         87 => error.InvalidParameter, // ERROR_INVALID_PARAMETER
         else => error.Win32Failure,
     };
 }

 pub fn checkBool(ok: BOOL) Error!void {
     if (succeeded(ok)) return;
     return mapLastError(lastError());
 }

 pub fn isValidWindow(hwnd: HWND) bool {
     if (hwnd == null) return false;
     return succeeded(IsWindow(hwnd));
 }

 pub fn getForegroundWindow() ?HWND {
     return GetForegroundWindow();
 }

 pub fn getForegroundProcessId() ?DWORD {
     const hwnd = getForegroundWindow() orelse return null;
     return getWindowProcessId(hwnd) catch null;
 }

 pub fn getWindowProcessId(hwnd: HWND) Error!DWORD {
     if (!isValidWindow(hwnd)) return error.InvalidWindow;

     var pid: DWORD = 0;
     _ = GetWindowThreadProcessId(hwnd, &pid);
     if (pid == 0) return error.InvalidWindow;
     return pid;
 }

 pub fn getWindowThreadId(hwnd: HWND) Error!DWORD {
     if (!isValidWindow(hwnd)) return error.InvalidWindow;

     var pid: DWORD = 0;
     const tid = GetWindowThreadProcessId(hwnd, &pid);
     if (tid == 0) return error.InvalidWindow;
     return tid;
 }

 pub fn isWindowVisibleSafe(hwnd: HWND) bool {
     if (!isValidWindow(hwnd)) return false;
     return succeeded(IsWindowVisible(hwnd));
 }

 pub fn isWindowMinimized(hwnd: HWND) bool {
     if (!isValidWindow(hwnd)) return false;
     return succeeded(IsIconic(hwnd));
 }

 pub fn getWindowRectSafe(hwnd: HWND) Error!RECT {
     if (!isValidWindow(hwnd)) return error.InvalidWindow;

     var rect: RECT = undefined;
     try checkBool(GetWindowRect(hwnd, &rect));
     return rect;
 }

 pub fn getWindowPlacementSafe(hwnd: HWND) Error!WINDOWPLACEMENT {
     if (!isValidWindow(hwnd)) return error.InvalidWindow;

     var placement = WINDOWPLACEMENT{
         .length = @sizeOf(WINDOWPLACEMENT),
         .flags = 0,
         .showCmd = 0,
         .ptMinPosition = .{ .x = 0, .y = 0 },
         .ptMaxPosition = .{ .x = 0, .y = 0 },
         .rcNormalPosition = .{
             .left = 0,
             .top = 0,
             .right = 0,
             .bottom = 0,
         },
     };

     try checkBool(GetWindowPlacement(hwnd, &placement));
     return placement;
 }

 pub fn restoreWindow(hwnd: HWND) Error!void {
     if (!isValidWindow(hwnd)) return error.InvalidWindow;
     _ = ShowWindow(hwnd, SW_RESTORE);
 }

 pub fn activateWindow(hwnd: HWND) Error!void {
     if (!isValidWindow(hwnd)) return error.InvalidWindow;
     try checkBool(SetForegroundWindow(hwnd));
 }

 pub fn findWindowByTitle(title: [*:0]const WCHAR) ?HWND {
     return FindWindowW(null, title);
 }

 pub fn getWindowStyle(hwnd: HWND) Error!LONG {
     if (!isValidWindow(hwnd)) return error.InvalidWindow;
     return GetWindowLongW(hwnd, GWL_STYLE);
 }

 pub fn isLikelyUserVisibleWindow(hwnd: HWND) bool {
     if (!isValidWindow(hwnd)) return false;
     if (!isWindowVisibleSafe(hwnd)) return false;

     const style = getWindowStyle(hwnd) catch return false;
     if ((style & WS_DISABLED) != 0) return false;

     const rect = getWindowRectSafe(hwnd) catch return false;
     return !rect.isEmpty();
 }

 pub fn getNearestMonitor(hwnd: HWND) ?HMONITOR {
     if (!isValidWindow(hwnd)) return null;
     return MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
 }

 pub fn getMonitorInfoSafe(monitor: HMONITOR) Error!MONITORINFO {
     if (monitor == null) return error.InvalidParameter;

     var info = MONITORINFO{
         .cbSize = @sizeOf(MONITORINFO),
         .rcMonitor = .{ .left = 0, .top = 0, .right = 0, .bottom = 0 },
         .rcWork = .{ .left = 0, .top = 0, .right = 0, .bottom = 0 },
         .dwFlags = 0,
     };

     try checkBool(GetMonitorInfoW(monitor, &info));
     return info;
 }

 pub fn queryForegroundWindowInfo() ?ForegroundWindowInfo {
     const hwnd = getForegroundWindow() orelse return null;
     if (!isValidWindow(hwnd)) return null;

     var pid: DWORD = 0;
     const tid = GetWindowThreadProcessId(hwnd, &pid);
     if (tid == 0 or pid == 0) return null;

     const rect = getWindowRectSafe(hwnd) catch return null;

     return .{
         .hwnd = hwnd,
         .process_id = pid,
         .thread_id = tid,
         .visible = isWindowVisibleSafe(hwnd),
         .minimized = isWindowMinimized(hwnd),
         .rect = rect,
     };
 }

 pub fn isForegroundProcess(pid: DWORD) bool {
     const fg_pid = getForegroundProcessId() orelse return false;
     return fg_pid == pid;
 }

 test "rect width and height are computed correctly" {
     const rect = RECT{
         .left = 10,
         .top = 20,
         .right = 110,
         .bottom = 70,
     };

     try std.testing.expectEqual(@as(LONG, 100), rect.width());
     try std.testing.expectEqual(@as(LONG, 50), rect.height());
     try std.testing.expect(!rect.isEmpty());
 }

 test "empty rect is detected" {
     const rect = RECT{
         .left = 5,
         .top = 5,
         .right = 5,
         .bottom = 10,
     };

     try std.testing.expect(rect.isEmpty());
 }

 test "bool helpers behave as expected" {
     try std.testing.expect(succeeded(TRUE));
     try std.testing.expect(!succeeded(FALSE));
     try std.testing.expect(failed(FALSE));
     try std.testing.expect(!failed(TRUE));
 }
