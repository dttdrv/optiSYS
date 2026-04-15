//! Minimal Windows `kernel32` interop scaffold for the optiSYS Zig core.
//!
//! This file intentionally focuses on a small, stable subset of Win32 APIs
//! needed by the first backend-only milestones:
//! - process information tuning
//! - memory resource notifications
//! - power setting notifications
//! - basic handle lifecycle
//! - event creation/signaling
//!
//! Design goals:
//! - keep ABI declarations explicit and easy to audit
//! - avoid pulling in broad translated headers too early
//! - provide small helper wrappers with Zig-friendly error handling
//! - stay suitable for a future service/daemon style backend

const std = @import("std");

pub const BOOL = i32;
pub const BYTE = u8;
pub const WORD = u16;
pub const DWORD = u32;
pub const ULONG = u32;
pub const ULONG_PTR = usize;
pub const UINT = u32;
pub const HANDLE = ?*anyopaque;
pub const HWND = ?*anyopaque;
pub const HPOWERNOTIFY = ?*anyopaque;
pub const LPVOID = ?*anyopaque;
pub const LPCVOID = ?*const anyopaque;
pub const WCHAR = u16;
pub const LPCWSTR = ?[*:0]const WCHAR;
pub const LPWSTR = ?[*:0]WCHAR;

pub const INVALID_HANDLE_VALUE: HANDLE = @ptrFromInt(@as(usize, @bitCast(@as(isize, -1))));

pub const FALSE: BOOL = 0;
pub const TRUE: BOOL = 1;

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

pub const LOW_MEMORY_RESOURCE_NOTIFICATION: DWORD = 0;
pub const HIGH_MEMORY_RESOURCE_NOTIFICATION: DWORD = 1;

pub const PBT_POWERSETTINGCHANGE: DWORD = 0x8013;

pub const PROCESS_POWER_THROTTLING_CURRENT_VERSION: ULONG = 1;
pub const PROCESS_POWER_THROTTLING_EXECUTION_SPEED: ULONG = 0x1;
pub const PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION: ULONG = 0x4;

pub const MEMORY_PRIORITY_VERY_LOW: ULONG = 1;
pub const MEMORY_PRIORITY_LOW: ULONG = 2;
pub const MEMORY_PRIORITY_MEDIUM: ULONG = 3;
pub const MEMORY_PRIORITY_BELOW_NORMAL: ULONG = 4;
pub const MEMORY_PRIORITY_NORMAL: ULONG = 5;

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
    LowMemoryResourceNotification = LOW_MEMORY_RESOURCE_NOTIFICATION,
    HighMemoryResourceNotification = HIGH_MEMORY_RESOURCE_NOTIFICATION,
};

pub const MEMORY_PRIORITY_INFORMATION = extern struct {
    MemoryPriority: ULONG,
};

pub const PROCESS_POWER_THROTTLING_STATE = extern struct {
    Version: ULONG,
    ControlMask: ULONG,
    StateMask: ULONG,
};

pub const GUID = extern struct {
    Data1: u32,
    Data2: u16,
    Data3: u16,
    Data4: [8]u8,
};

pub const POWERBROADCAST_SETTING = extern struct {
    PowerSetting: GUID,
    DataLength: DWORD,
    Data: [1]u8,
};

pub const SYSTEM_POWER_STATUS = extern struct {
    ACLineStatus: BYTE,
    BatteryFlag: BYTE,
    BatteryLifePercent: BYTE,
    SystemStatusFlag: BYTE,
    BatteryLifeTime: DWORD,
    BatteryFullLifeTime: DWORD,
};

pub const Error = error{
    AccessDenied,
    InvalidHandle,
    InvalidParameter,
    NotSupported,
    OutOfMemory,
    Timeout,
    Unexpected,
    Win32Failure,
};

pub extern "kernel32" fn GetLastError() callconv(.winapi) DWORD;
pub extern "kernel32" fn CloseHandle(hObject: HANDLE) callconv(.winapi) BOOL;

pub extern "kernel32" fn OpenProcess(
    dwDesiredAccess: DWORD,
    bInheritHandle: BOOL,
    dwProcessId: DWORD,
) callconv(.winapi) HANDLE;

pub extern "kernel32" fn GetCurrentProcess() callconv(.winapi) HANDLE;
pub extern "kernel32" fn GetCurrentProcessId() callconv(.winapi) DWORD;

pub extern "kernel32" fn SetProcessInformation(
    hProcess: HANDLE,
    ProcessInformationClass: PROCESS_INFORMATION_CLASS,
    ProcessInformation: LPVOID,
    ProcessInformationSize: DWORD,
) callconv(.winapi) BOOL;

pub extern "kernel32" fn CreateMemoryResourceNotification(
    NotificationType: MEMORY_RESOURCE_NOTIFICATION_TYPE,
) callconv(.winapi) HANDLE;

pub extern "kernel32" fn QueryMemoryResourceNotification(
    ResourceNotificationHandle: HANDLE,
    ResourceState: *BOOL,
) callconv(.winapi) BOOL;

pub extern "kernel32" fn RegisterPowerSettingNotification(
    hRecipient: HANDLE,
    PowerSettingGuid: *const GUID,
    Flags: DWORD,
) callconv(.winapi) HPOWERNOTIFY;

pub extern "kernel32" fn UnregisterPowerSettingNotification(
    Handle: HPOWERNOTIFY,
) callconv(.winapi) BOOL;

pub extern "kernel32" fn GetSystemPowerStatus(
    lpSystemPowerStatus: *SYSTEM_POWER_STATUS,
) callconv(.winapi) BOOL;

pub extern "kernel32" fn CreateEventW(
    lpEventAttributes: LPVOID,
    bManualReset: BOOL,
    bInitialState: BOOL,
    lpName: LPCWSTR,
) callconv(.winapi) HANDLE;

pub extern "kernel32" fn SetEvent(hEvent: HANDLE) callconv(.winapi) BOOL;
pub extern "kernel32" fn ResetEvent(hEvent: HANDLE) callconv(.winapi) BOOL;
pub extern "kernel32" fn WaitForSingleObject(
    hHandle: HANDLE,
    dwMilliseconds: DWORD,
) callconv(.winapi) DWORD;

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
        6 => error.InvalidHandle, // ERROR_INVALID_HANDLE
        8 => error.OutOfMemory, // ERROR_NOT_ENOUGH_MEMORY
        87 => error.InvalidParameter, // ERROR_INVALID_PARAMETER
        50 => error.NotSupported, // ERROR_NOT_SUPPORTED
        else => error.Win32Failure,
    };
}

pub fn checkBool(ok: BOOL) Error!void {
    if (succeeded(ok)) return;
    return mapLastError(lastError());
}

pub fn closeHandle(handle: HANDLE) void {
    if (handle == null or handle == INVALID_HANDLE_VALUE) return;
    _ = CloseHandle(handle);
}

pub const OwnedHandle = struct {
    handle: HANDLE = null,

    pub fn init(handle: HANDLE) OwnedHandle {
        return .{ .handle = handle };
    }

    pub fn isValid(self: OwnedHandle) bool {
        return self.handle != null and self.handle != INVALID_HANDLE_VALUE;
    }

    pub fn deinit(self: *OwnedHandle) void {
        closeHandle(self.handle);
        self.handle = null;
    }
};

pub fn openProcessForTuning(pid: DWORD) Error!OwnedHandle {
    const handle = OpenProcess(
        PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE,
        FALSE,
        pid,
    );

    if (handle == null) {
        return mapLastError(lastError());
    }

    return OwnedHandle.init(handle);
}

pub fn setProcessMemoryPriority(handle: HANDLE, priority: ULONG) Error!void {
    var info = MEMORY_PRIORITY_INFORMATION{
        .MemoryPriority = priority,
    };

    try checkBool(SetProcessInformation(
        handle,
        .ProcessMemoryPriority,
        @ptrCast(&info),
        @sizeOf(MEMORY_PRIORITY_INFORMATION),
    ));
}

pub fn setProcessEcoQos(handle: HANDLE, enabled: bool) Error!void {
    var state = PROCESS_POWER_THROTTLING_STATE{
        .Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
        .ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
        .StateMask = if (enabled) PROCESS_POWER_THROTTLING_EXECUTION_SPEED else 0,
    };

    try checkBool(SetProcessInformation(
        handle,
        .ProcessPowerThrottling,
        @ptrCast(&state),
        @sizeOf(PROCESS_POWER_THROTTLING_STATE),
    ));
}

pub fn setProcessIgnoreTimerResolution(handle: HANDLE, enabled: bool) Error!void {
    var state = PROCESS_POWER_THROTTLING_STATE{
        .Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
        .ControlMask = PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION,
        .StateMask = if (enabled) PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION else 0,
    };

    try checkBool(SetProcessInformation(
        handle,
        .ProcessPowerThrottling,
        @ptrCast(&state),
        @sizeOf(PROCESS_POWER_THROTTLING_STATE),
    ));
}

pub fn createLowMemoryNotification() Error!OwnedHandle {
    const handle = CreateMemoryResourceNotification(.LowMemoryResourceNotification);
    if (handle == null) {
        return mapLastError(lastError());
    }
    return OwnedHandle.init(handle);
}

pub fn createHighMemoryNotification() Error!OwnedHandle {
    const handle = CreateMemoryResourceNotification(.HighMemoryResourceNotification);
    if (handle == null) {
        return mapLastError(lastError());
    }
    return OwnedHandle.init(handle);
}

pub fn queryMemoryNotification(handle: HANDLE) Error!bool {
    var state: BOOL = FALSE;
    try checkBool(QueryMemoryResourceNotification(handle, &state));
    return succeeded(state);
}

pub fn getSystemPowerStatus() Error!SYSTEM_POWER_STATUS {
    var status: SYSTEM_POWER_STATUS = undefined;
    try checkBool(GetSystemPowerStatus(&status));
    return status;
}

pub fn createManualResetEvent(name: ?[*:0]const WCHAR, initial_state: bool) Error!OwnedHandle {
    const handle = CreateEventW(
        null,
        TRUE,
        if (initial_state) TRUE else FALSE,
        name,
    );

    if (handle == null) {
        return mapLastError(lastError());
    }

    return OwnedHandle.init(handle);
}

pub fn signalEvent(handle: HANDLE) Error!void {
    try checkBool(SetEvent(handle));
}

pub fn clearEvent(handle: HANDLE) Error!void {
    try checkBool(ResetEvent(handle));
}

pub fn waitForSingle(handle: HANDLE, timeout_ms: DWORD) Error!void {
    const result = WaitForSingleObject(handle, timeout_ms);
    switch (result) {
        WAIT_OBJECT_0 => return,
        WAIT_TIMEOUT => return error.Timeout,
        WAIT_FAILED => return mapLastError(lastError()),
        else => return error.Unexpected,
    }
}

pub const OwnedPowerNotify = struct {
    handle: HPOWERNOTIFY = null,

    pub fn init(handle: HPOWERNOTIFY) OwnedPowerNotify {
        return .{ .handle = handle };
    }

    pub fn isValid(self: OwnedPowerNotify) bool {
        return self.handle != null;
    }

    pub fn deinit(self: *OwnedPowerNotify) void {
        if (self.handle) |h| {
            _ = UnregisterPowerSettingNotification(h);
        }
        self.handle = null;
    }
};

pub fn registerPowerSettingNotification(
    recipient: HANDLE,
    setting_guid: *const GUID,
    flags: DWORD,
) Error!OwnedPowerNotify {
    const handle = RegisterPowerSettingNotification(recipient, setting_guid, flags);
    if (handle == null) {
        return mapLastError(lastError());
    }
    return OwnedPowerNotify.init(handle);
}

/// Common power setting GUIDs useful for the optiSYS backend.
///
/// These are intentionally limited to the first set of notifications we are
/// likely to consume in the Zig core.
pub const power_guid = struct {
    /// GUID_ACDC_POWER_SOURCE
    pub const acdc_power_source = GUID{
        .Data1 = 0x5D3E9A59,
        .Data2 = 0xE9D5,
        .Data3 = 0x4B00,
        .Data4 = .{ 0xA6, 0xBD, 0xFF, 0x34, 0xFF, 0x51, 0x65, 0x48 },
    };

    /// GUID_BATTERY_PERCENTAGE_REMAINING
    pub const battery_percentage_remaining = GUID{
        .Data1 = 0xA7AD8041,
        .Data2 = 0xB45A,
        .Data3 = 0x4CAE,
        .Data4 = .{ 0x87, 0xA3, 0xEE, 0xCB, 0xB4, 0x68, 0xA9, 0xE1 },
    };

    /// GUID_CONSOLE_DISPLAY_STATE
    pub const console_display_state = GUID{
        .Data1 = 0x6FE69556,
        .Data2 = 0x704A,
        .Data3 = 0x47A0,
        .Data4 = .{ 0x8F, 0x24, 0xC2, 0x8D, 0x93, 0x6F, 0xDA, 0x47 },
    };

    /// GUID_SESSION_USER_PRESENCE
    pub const session_user_presence = GUID{
        .Data1 = 0x3C0F4548,
        .Data2 = 0xC03F,
        .Data3 = 0x4C4D,
        .Data4 = .{ 0xB9, 0xF2, 0x23, 0x7E, 0xDE, 0x68, 0x63, 0x76 },
    };
};

test "bool helpers behave as expected" {
    try std.testing.expect(succeeded(TRUE));
    try std.testing.expect(!succeeded(FALSE));
    try std.testing.expect(failed(FALSE));
    try std.testing.expect(!failed(TRUE));
}

test "owned handle validity rules" {
    var null_handle = OwnedHandle.init(null);
    defer null_handle.deinit();
    try std.testing.expect(!null_handle.isValid());
}
