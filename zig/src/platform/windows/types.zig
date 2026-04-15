 //! Shared Windows ABI type aliases for the optiSYS Zig backend.
 //!
 //! This module intentionally stays small and dependency-light so it can be
 //! imported by all Windows interop wrappers without creating circular imports.
 //! It models only the common aliases and structs needed by the first backend
 //! milestones.

 const std = @import("std");

 pub const VOID = void;
 pub const BOOL = i32;
 pub const BOOLEAN = u8;
 pub const BYTE = u8;
 pub const CHAR = i8;
 pub const UCHAR = u8;
 pub const SHORT = i16;
 pub const USHORT = u16;
 pub const WORD = u16;
 pub const INT = i32;
 pub const UINT = u32;
 pub const LONG = i32;
 pub const ULONG = u32;
 pub const DWORD = u32;
 pub const DWORDLONG = u64;
 pub const LONGLONG = i64;
 pub const ULONGLONG = u64;
 pub const HRESULT = i32;
 pub const NTSTATUS = i32;
 pub const SIZE_T = usize;
 pub const SSIZE_T = isize;
 pub const ULONG_PTR = usize;
 pub const DWORD_PTR = usize;
 pub const UINT_PTR = usize;
 pub const LONG_PTR = isize;
 pub const LPARAM = isize;
 pub const WPARAM = usize;
 pub const LRESULT = isize;

 pub const HANDLE = ?*anyopaque;
 pub const HINSTANCE = HANDLE;
 pub const HMODULE = HANDLE;
 pub const HWND = HANDLE;
 pub const HPOWERNOTIFY = HANDLE;
 pub const HLOCAL = HANDLE;
 pub const HGLOBAL = HANDLE;

 pub const LPVOID = ?*anyopaque;
 pub const LPCVOID = ?*const anyopaque;

 pub const WCHAR = u16;
 pub const PWSTR = ?[*:0]WCHAR;
 pub const PCWSTR = ?[*:0]const WCHAR;
 pub const LPWSTR = PWSTR;
 pub const LPCWSTR = PCWSTR;

 pub const PSTR = ?[*:0]u8;
 pub const PCSTR = ?[*:0]const u8;
 pub const LPSTR = PSTR;
 pub const LPCSTR = PCSTR;

 pub const FALSE: BOOL = 0;
 pub const TRUE: BOOL = 1;

 pub const INVALID_HANDLE_VALUE: HANDLE = @ptrFromInt(@as(usize, @bitCast(@as(isize, -1))));

 pub const GUID = extern struct {
     Data1: u32,
     Data2: u16,
     Data3: u16,
     Data4: [8]u8,

     pub fn eql(a: GUID, b: GUID) bool {
         return a.Data1 == b.Data1 and
             a.Data2 == b.Data2 and
             a.Data3 == b.Data3 and
             std.mem.eql(u8, &a.Data4, &b.Data4);
     }
 };

 pub const FILETIME = extern struct {
     dwLowDateTime: DWORD,
     dwHighDateTime: DWORD,
 };

 pub const SYSTEMTIME = extern struct {
     wYear: WORD,
     wMonth: WORD,
     wDayOfWeek: WORD,
     wDay: WORD,
     wHour: WORD,
     wMinute: WORD,
     wSecond: WORD,
     wMilliseconds: WORD,
 };

 pub const LARGE_INTEGER = extern union {
     QuadPart: i64,
 };

 pub const ULARGE_INTEGER = extern union {
     QuadPart: u64,
 };

 pub const LUID = extern struct {
     LowPart: DWORD,
     HighPart: LONG,
 };

 pub const RECT = extern struct {
     left: LONG,
     top: LONG,
     right: LONG,
     bottom: LONG,
 };

 pub const POINT = extern struct {
     x: LONG,
     y: LONG,
 };

 pub const SYSTEM_POWER_STATUS = extern struct {
     ACLineStatus: BYTE,
     BatteryFlag: BYTE,
     BatteryLifePercent: BYTE,
     SystemStatusFlag: BYTE,
     BatteryLifeTime: DWORD,
     BatteryFullLifeTime: DWORD,
 };

 pub const POWERBROADCAST_SETTING = extern struct {
     PowerSetting: GUID,
     DataLength: DWORD,
     Data: [1]BYTE,
 };

 pub const MEMORYSTATUSEX = extern struct {
     dwLength: DWORD,
     dwMemoryLoad: DWORD,
     ullTotalPhys: DWORDLONG,
     ullAvailPhys: DWORDLONG,
     ullTotalPageFile: DWORDLONG,
     ullAvailPageFile: DWORDLONG,
     ullTotalVirtual: DWORDLONG,
     ullAvailVirtual: DWORDLONG,
     ullAvailExtendedVirtual: DWORDLONG,
 };

 pub const MEMORY_PRIORITY_INFORMATION = extern struct {
     MemoryPriority: ULONG,
 };

 pub const PROCESS_POWER_THROTTLING_STATE = extern struct {
     Version: ULONG,
     ControlMask: ULONG,
     StateMask: ULONG,
 };

 pub const PROCESS_INFORMATION_CLASS = enum(c_int) {
     ProcessMemoryPriority = 0,
     ProcessMemoryExhaustionInfo = 1,
     ProcessAppMemoryInfo = 2,
     ProcessInPrivateInfo = 3,
     ProcessPowerThrottling = 4,
     ProcessReservedValue1 = 5,
     ProcessTelemetryCoverageInfo = 6,
     ProcessProtectionLevelInfo = 7,
     ProcessLeapSecondInfo = 8,
     ProcessMachineTypeInfo = 9,
     ProcessOverrideSubsequentPrefetchParameter = 10,
     ProcessMaxOverridePrefetchParameter = 11,
     ProcessInformationClassMax = 12,
 };

 pub const MEMORY_RESOURCE_NOTIFICATION_TYPE = enum(DWORD) {
     LowMemoryResourceNotification = 0,
     HighMemoryResourceNotification = 1,
 };

 pub const WAIT_OBJECT_0: DWORD = 0x00000000;
 pub const WAIT_TIMEOUT: DWORD = 0x00000102;
 pub const WAIT_FAILED: DWORD = 0xFFFFFFFF;
 pub const INFINITE: DWORD = 0xFFFFFFFF;

 pub const PROCESS_SET_INFORMATION: DWORD = 0x0200;
 pub const PROCESS_QUERY_INFORMATION: DWORD = 0x0400;
 pub const PROCESS_QUERY_LIMITED_INFORMATION: DWORD = 0x1000;
 pub const SYNCHRONIZE: DWORD = 0x00100000;

 pub const EVENT_MODIFY_STATE: DWORD = 0x0002;

 pub const DEVICE_NOTIFY_WINDOW_HANDLE: DWORD = 0x00000000;
 pub const DEVICE_NOTIFY_SERVICE_HANDLE: DWORD = 0x00000001;

 pub const PBT_POWERSETTINGCHANGE: DWORD = 0x8013;

 pub const PROCESS_POWER_THROTTLING_CURRENT_VERSION: ULONG = 1;
 pub const PROCESS_POWER_THROTTLING_EXECUTION_SPEED: ULONG = 0x1;
 pub const PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION: ULONG = 0x4;

 pub const MEMORY_PRIORITY_VERY_LOW: ULONG = 1;
 pub const MEMORY_PRIORITY_LOW: ULONG = 2;
 pub const MEMORY_PRIORITY_MEDIUM: ULONG = 3;
 pub const MEMORY_PRIORITY_BELOW_NORMAL: ULONG = 4;
 pub const MEMORY_PRIORITY_NORMAL: ULONG = 5;

 pub fn succeeded(value: BOOL) bool {
     return value != FALSE;
 }

 pub fn failed(value: BOOL) bool {
     return value == FALSE;
 }

 pub fn isValidHandle(handle: HANDLE) bool {
     return handle != null and handle != INVALID_HANDLE_VALUE;
 }

 test "guid equality works" {
     const a = GUID{
         .Data1 = 1,
         .Data2 = 2,
         .Data3 = 3,
         .Data4 = .{ 4, 5, 6, 7, 8, 9, 10, 11 },
     };
     const b = GUID{
         .Data1 = 1,
         .Data2 = 2,
         .Data3 = 3,
         .Data4 = .{ 4, 5, 6, 7, 8, 9, 10, 11 },
     };
     const c = GUID{
         .Data1 = 9,
         .Data2 = 2,
         .Data3 = 3,
         .Data4 = .{ 4, 5, 6, 7, 8, 9, 10, 11 },
     };

     try std.testing.expect(GUID.eql(a, b));
     try std.testing.expect(!GUID.eql(a, c));
 }

 test "handle validity helper rejects null and invalid sentinel" {
     try std.testing.expect(!isValidHandle(null));
     try std.testing.expect(!isValidHandle(INVALID_HANDLE_VALUE));
 }

 test "bool helpers match win32 semantics" {
     try std.testing.expect(succeeded(TRUE));
     try std.testing.expect(!succeeded(FALSE));
     try std.testing.expect(failed(FALSE));
     try std.testing.expect(!failed(TRUE));
 }
