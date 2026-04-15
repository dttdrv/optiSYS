 //! Minimal Windows `advapi32` service-control interop scaffold for the optiSYS Zig core.
 //!
 //! This module is intentionally narrow and backend-focused. It provides:
 //! - Service Control Manager access
 //! - Service open/query/start/stop/config primitives
 //! - Process-aware service status querying
 //! - Wait helpers for pending service transitions
 //!
 //! Design goals:
 //! - explicit ABI declarations
 //! - small Zig-friendly wrappers
 //! - no policy logic
 //! - safe handle ownership
 //! - suitability for future transactional service actions

const std = @import("std");

pub const BOOL = i32;
pub const BYTE = u8;
pub const DWORD = u32;
pub const ULONG = u32;
pub const HANDLE = ?*anyopaque;
pub const SC_HANDLE = ?*anyopaque;
pub const LPVOID = ?*anyopaque;
pub const LPCVOID = ?*const anyopaque;
pub const WCHAR = u16;
pub const LPCWSTR = ?[*:0]const WCHAR;
pub const LPWSTR = ?[*:0]WCHAR;

pub const FALSE: BOOL = 0;
pub const TRUE: BOOL = 1;

pub const SC_MANAGER_CONNECT: DWORD = 0x0001;
pub const SC_MANAGER_CREATE_SERVICE: DWORD = 0x0002;
pub const SC_MANAGER_ENUMERATE_SERVICE: DWORD = 0x0004;
pub const SC_MANAGER_LOCK: DWORD = 0x0008;
pub const SC_MANAGER_QUERY_LOCK_STATUS: DWORD = 0x0010;
pub const SC_MANAGER_MODIFY_BOOT_CONFIG: DWORD = 0x0020;
pub const SC_MANAGER_ALL_ACCESS: DWORD = 0xF003F;

pub const SERVICE_QUERY_CONFIG: DWORD = 0x0001;
pub const SERVICE_CHANGE_CONFIG: DWORD = 0x0002;
pub const SERVICE_QUERY_STATUS: DWORD = 0x0004;
pub const SERVICE_ENUMERATE_DEPENDENTS: DWORD = 0x0008;
pub const SERVICE_START: DWORD = 0x0010;
pub const SERVICE_STOP: DWORD = 0x0020;
pub const SERVICE_PAUSE_CONTINUE: DWORD = 0x0040;
pub const SERVICE_INTERROGATE: DWORD = 0x0080;
pub const SERVICE_USER_DEFINED_CONTROL: DWORD = 0x0100;
pub const SERVICE_ALL_ACCESS: DWORD = 0xF01FF;

pub const SERVICE_NO_CHANGE: DWORD = 0xFFFFFFFF;

pub const SERVICE_BOOT_START: DWORD = 0x00000000;
pub const SERVICE_SYSTEM_START: DWORD = 0x00000001;
pub const SERVICE_AUTO_START: DWORD = 0x00000002;
pub const SERVICE_DEMAND_START: DWORD = 0x00000003;
pub const SERVICE_DISABLED: DWORD = 0x00000004;

pub const SERVICE_CONTROL_STOP: DWORD = 0x00000001;

pub const SERVICE_STOPPED: DWORD = 0x00000001;
pub const SERVICE_START_PENDING: DWORD = 0x00000002;
pub const SERVICE_STOP_PENDING: DWORD = 0x00000003;
pub const SERVICE_RUNNING: DWORD = 0x00000004;
pub const SERVICE_CONTINUE_PENDING: DWORD = 0x00000005;
pub const SERVICE_PAUSE_PENDING: DWORD = 0x00000006;
pub const SERVICE_PAUSED: DWORD = 0x00000007;

pub const SERVICE_ACCEPT_STOP: DWORD = 0x00000001;

pub const SC_STATUS_PROCESS_INFO: DWORD = 0;

pub const ERROR_INSUFFICIENT_BUFFER: DWORD = 122;
pub const ERROR_SERVICE_ALREADY_RUNNING: DWORD = 1056;
pub const ERROR_SERVICE_DISABLED: DWORD = 1058;
pub const ERROR_SERVICE_DOES_NOT_EXIST: DWORD = 1060;
pub const ERROR_SERVICE_CANNOT_ACCEPT_CTRL: DWORD = 1061;
pub const ERROR_SERVICE_NOT_ACTIVE: DWORD = 1062;
pub const ERROR_SERVICE_REQUEST_TIMEOUT: DWORD = 1053;

pub const SERVICE_STATUS = extern struct {
    dwServiceType: DWORD,
    dwCurrentState: DWORD,
    dwControlsAccepted: DWORD,
    dwWin32ExitCode: DWORD,
    dwServiceSpecificExitCode: DWORD,
    dwCheckPoint: DWORD,
    dwWaitHint: DWORD,
};

pub const SERVICE_STATUS_PROCESS = extern struct {
    dwServiceType: DWORD,
    dwCurrentState: DWORD,
    dwControlsAccepted: DWORD,
    dwWin32ExitCode: DWORD,
    dwServiceSpecificExitCode: DWORD,
    dwCheckPoint: DWORD,
    dwWaitHint: DWORD,
    dwProcessId: DWORD,
    dwServiceFlags: DWORD,
};

pub const QUERY_SERVICE_CONFIGW = extern struct {
    dwServiceType: DWORD,
    dwStartType: DWORD,
    dwErrorControl: DWORD,
    lpBinaryPathName: LPWSTR,
    lpLoadOrderGroup: LPWSTR,
    dwTagId: DWORD,
    lpDependencies: LPWSTR,
    lpServiceStartName: LPWSTR,
    lpDisplayName: LPWSTR,
};

pub const Error = error{
    AccessDenied,
    InvalidHandle,
    InvalidParameter,
    NotSupported,
    OutOfMemory,
    Timeout,
    ServiceDoesNotExist,
    ServiceDisabled,
    ServiceAlreadyRunning,
    ServiceCannotAcceptControl,
    ServiceNotActive,
    InsufficientBuffer,
    Win32Failure,
    Unexpected,
};

pub extern "advapi32" fn OpenSCManagerW(
    lpMachineName: LPCWSTR,
    lpDatabaseName: LPCWSTR,
    dwDesiredAccess: DWORD,
) callconv(.winapi) SC_HANDLE;

pub extern "advapi32" fn OpenServiceW(
    hSCManager: SC_HANDLE,
    lpServiceName: LPCWSTR,
    dwDesiredAccess: DWORD,
) callconv(.winapi) SC_HANDLE;

pub extern "advapi32" fn CloseServiceHandle(
    hSCObject: SC_HANDLE,
) callconv(.winapi) BOOL;

pub extern "advapi32" fn QueryServiceStatus(
    hService: SC_HANDLE,
    lpServiceStatus: *SERVICE_STATUS,
) callconv(.winapi) BOOL;

pub extern "advapi32" fn QueryServiceStatusEx(
    hService: SC_HANDLE,
    InfoLevel: DWORD,
    lpBuffer: LPVOID,
    cbBufSize: DWORD,
    pcbBytesNeeded: *DWORD,
) callconv(.winapi) BOOL;

pub extern "advapi32" fn QueryServiceConfigW(
    hService: SC_HANDLE,
    lpServiceConfig: LPVOID,
    cbBufSize: DWORD,
    pcbBytesNeeded: *DWORD,
) callconv(.winapi) BOOL;

pub extern "advapi32" fn ChangeServiceConfigW(
    hService: SC_HANDLE,
    dwServiceType: DWORD,
    dwStartType: DWORD,
    dwErrorControl: DWORD,
    lpBinaryPathName: LPCWSTR,
    lpLoadOrderGroup: LPCWSTR,
    lpdwTagId: LPVOID,
    lpDependencies: LPCWSTR,
    lpServiceStartName: LPCWSTR,
    lpPassword: LPCWSTR,
    lpDisplayName: LPCWSTR,
) callconv(.winapi) BOOL;

pub extern "advapi32" fn ControlService(
    hService: SC_HANDLE,
    dwControl: DWORD,
    lpServiceStatus: *SERVICE_STATUS,
) callconv(.winapi) BOOL;

pub extern "advapi32" fn StartServiceW(
    hService: SC_HANDLE,
    dwNumServiceArgs: DWORD,
    lpServiceArgVectors: LPVOID,
) callconv(.winapi) BOOL;

pub extern "kernel32" fn GetLastError() callconv(.winapi) DWORD;
pub extern "kernel32" fn Sleep(dwMilliseconds: DWORD) callconv(.winapi) void;

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
        5 => error.AccessDenied,
        6 => error.InvalidHandle,
        8 => error.OutOfMemory,
        87 => error.InvalidParameter,
        50 => error.NotSupported,
        ERROR_INSUFFICIENT_BUFFER => error.InsufficientBuffer,
        ERROR_SERVICE_DOES_NOT_EXIST => error.ServiceDoesNotExist,
        ERROR_SERVICE_DISABLED => error.ServiceDisabled,
        ERROR_SERVICE_ALREADY_RUNNING => error.ServiceAlreadyRunning,
        ERROR_SERVICE_CANNOT_ACCEPT_CTRL => error.ServiceCannotAcceptControl,
        ERROR_SERVICE_NOT_ACTIVE => error.ServiceNotActive,
        ERROR_SERVICE_REQUEST_TIMEOUT => error.Timeout,
        else => error.Win32Failure,
    };
}

pub fn checkBool(ok: BOOL) Error!void {
    if (succeeded(ok)) return;
    return mapLastError(lastError());
}

pub fn closeServiceHandle(handle: SC_HANDLE) void {
    if (handle == null) return;
    _ = CloseServiceHandle(handle);
}

pub const OwnedScHandle = struct {
    handle: SC_HANDLE = null,

    pub fn init(handle: SC_HANDLE) OwnedScHandle {
        return .{ .handle = handle };
    }

    pub fn isValid(self: OwnedScHandle) bool {
        return self.handle != null;
    }

    pub fn deinit(self: *OwnedScHandle) void {
        closeServiceHandle(self.handle);
        self.handle = null;
    }
};

pub fn openScManager(desired_access: DWORD) Error!OwnedScHandle {
    const handle = OpenSCManagerW(null, null, desired_access);
    if (handle == null) {
        return mapLastError(lastError());
    }
    return OwnedScHandle.init(handle);
}

pub fn openService(
    manager: SC_HANDLE,
    service_name: [*:0]const WCHAR,
    desired_access: DWORD,
) Error!OwnedScHandle {
    const handle = OpenServiceW(manager, service_name, desired_access);
    if (handle == null) {
        return mapLastError(lastError());
    }
    return OwnedScHandle.init(handle);
}

pub fn queryServiceStatus(service: SC_HANDLE) Error!SERVICE_STATUS {
    var status: SERVICE_STATUS = undefined;
    try checkBool(QueryServiceStatus(service, &status));
    return status;
}

pub fn queryServiceStatusProcess(service: SC_HANDLE) Error!SERVICE_STATUS_PROCESS {
    var status: SERVICE_STATUS_PROCESS = undefined;
    var bytes_needed: DWORD = 0;

    try checkBool(QueryServiceStatusEx(
        service,
        SC_STATUS_PROCESS_INFO,
        @ptrCast(&status),
        @sizeOf(SERVICE_STATUS_PROCESS),
        &bytes_needed,
    ));

    return status;
}

pub fn queryServiceStartType(service: SC_HANDLE) Error!DWORD {
    var bytes_needed: DWORD = 0;
    _ = QueryServiceConfigW(service, null, 0, &bytes_needed);

    if (bytes_needed == 0) {
        return mapLastError(lastError());
    }

    const allocator = std.heap.page_allocator;
    const buffer = try allocator.alloc(u8, bytes_needed);
    defer allocator.free(buffer);

    try checkBool(QueryServiceConfigW(
        service,
        @ptrCast(buffer.ptr),
        bytes_needed,
        &bytes_needed,
    ));

    const config: *const QUERY_SERVICE_CONFIGW = @ptrCast(@alignCast(buffer.ptr));
    return config.dwStartType;
}

pub fn changeServiceStartType(service: SC_HANDLE, start_type: DWORD) Error!void {
    try checkBool(ChangeServiceConfigW(
        service,
        SERVICE_NO_CHANGE,
        start_type,
        SERVICE_NO_CHANGE,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
    ));
}

pub fn sendStop(service: SC_HANDLE) Error!void {
    var status: SERVICE_STATUS = undefined;
    try checkBool(ControlService(service, SERVICE_CONTROL_STOP, &status));
}

pub fn startService(service: SC_HANDLE) Error!void {
    try checkBool(StartServiceW(service, 0, null));
}

pub fn isPendingState(state: DWORD) bool {
    return switch (state) {
        SERVICE_START_PENDING,
        SERVICE_STOP_PENDING,
        SERVICE_CONTINUE_PENDING,
        SERVICE_PAUSE_PENDING,
        => true,
        else => false,
    };
}

pub fn waitForServiceState(
    service: SC_HANDLE,
    desired_state: DWORD,
    timeout_ms: u32,
    poll_interval_ms: u32,
) Error!void {
    const start_ms = std.time.milliTimestamp();
    var last_checkpoint: DWORD = 0;
    var last_progress_ms: i64 = start_ms;

    while (true) {
        const status = try queryServiceStatusProcess(service);

        if (status.dwCurrentState == desired_state) {
            return;
        }

        const now_ms = std.time.milliTimestamp();
        if (@as(u64, @intCast(now_ms - start_ms)) >= timeout_ms) {
            return error.Timeout;
        }

        if (status.dwCheckPoint != 0) {
            if (status.dwCheckPoint != last_checkpoint) {
                last_checkpoint = status.dwCheckPoint;
                last_progress_ms = now_ms;
            } else {
                const wait_hint_ms: u64 = @max(@as(u64, status.dwWaitHint), 1000);
                if (@as(u64, @intCast(now_ms - last_progress_ms)) > wait_hint_ms) {
                    return error.Timeout;
                }
            }
        }

        Sleep(@max(poll_interval_ms, 50));
    }
}

pub fn stopServiceAndWait(
    service: SC_HANDLE,
    timeout_ms: u32,
    poll_interval_ms: u32,
) Error!void {
    const status = try queryServiceStatusProcess(service);

    switch (status.dwCurrentState) {
        SERVICE_STOPPED => return,
        SERVICE_STOP_PENDING => return waitForServiceState(service, SERVICE_STOPPED, timeout_ms, poll_interval_ms),
        SERVICE_RUNNING => {},
        else => return error.ServiceCannotAcceptControl,
    }

    if ((status.dwControlsAccepted & SERVICE_ACCEPT_STOP) == 0) {
        return error.ServiceCannotAcceptControl;
    }

    try sendStop(service);
    try waitForServiceState(service, SERVICE_STOPPED, timeout_ms, poll_interval_ms);
}

pub fn startServiceAndWait(
    service: SC_HANDLE,
    timeout_ms: u32,
    poll_interval_ms: u32,
) Error!void {
    const status = try queryServiceStatusProcess(service);

    switch (status.dwCurrentState) {
        SERVICE_RUNNING => return,
        SERVICE_START_PENDING => return waitForServiceState(service, SERVICE_RUNNING, timeout_ms, poll_interval_ms),
        SERVICE_STOPPED => {},
        else => return error.ServiceCannotAcceptControl,
    }

    startService(service) catch |err| switch (err) {
        error.ServiceAlreadyRunning => return,
        else => return err,
    };

    try waitForServiceState(service, SERVICE_RUNNING, timeout_ms, poll_interval_ms);
}

pub fn stateString(state: DWORD) []const u8 {
    return switch (state) {
        SERVICE_STOPPED => "stopped",
        SERVICE_START_PENDING => "start_pending",
        SERVICE_STOP_PENDING => "stop_pending",
        SERVICE_RUNNING => "running",
        SERVICE_CONTINUE_PENDING => "continue_pending",
        SERVICE_PAUSE_PENDING => "pause_pending",
        SERVICE_PAUSED => "paused",
        else => "unknown",
    };
}

test "bool helpers behave as expected" {
    try std.testing.expect(succeeded(TRUE));
    try std.testing.expect(!succeeded(FALSE));
    try std.testing.expect(failed(FALSE));
    try std.testing.expect(!failed(TRUE));
}

test "owned service handle validity rules" {
    var handle = OwnedScHandle.init(null);
    defer handle.deinit();
    try std.testing.expect(!handle.isValid());
}

test "pending state detection works" {
    try std.testing.expect(isPendingState(SERVICE_START_PENDING));
    try std.testing.expect(isPendingState(SERVICE_STOP_PENDING));
    try std.testing.expect(!isPendingState(SERVICE_RUNNING));
}

test "state string mapping is stable" {
    try std.testing.expectEqualStrings("running", stateString(SERVICE_RUNNING));
    try std.testing.expectEqualStrings("stopped", stateString(SERVICE_STOPPED));
}
