//! C ABI bridge for .NET P/Invoke interop.
//! Exposes optiSYS Zig core modules as a shared library (optisys_core.dll).
//!
//! Build: zig build -Dtarget=x86_64-windows-gnu -Doptimize=ReleaseFast
//! Produces: optisys_core.dll

const std = @import("std");
const power = @import("../modules/power/power.zig");
const memory = @import("../modules/memory/memory.zig");
const process = @import("../modules/process/process.zig");
const types = @import("../core/types.zig");

// ── Extern structs matching C# NativeBatteryInfo ─────────────────────

pub const CBatteryInfo = extern struct {
    power_source: c_int, // 0=Unknown, 1=AC, 2=Battery
    has_battery: bool,
    charge_percent: u8,
    drain_rate_milliwatts: c_int,
    estimated_time_remaining_seconds: c_int,
};

pub const CMemoryInfo = extern struct {
    total_physical_bytes: i64,
    available_physical_bytes: i64,
    committed_bytes: i64,
    standby_cache_bytes: i64,
    modified_page_list_bytes: i64,
};

pub const CProcessInfo = extern struct {
    process_id: c_int,
    process_name: [256]u8,
    working_set_bytes: i64,
    private_bytes: i64,
    priority_class: c_int,
    is_foreground: bool,
    is_excluded: bool,
};

pub const CPowerSourceInfo = extern struct {
    power_source: c_int,
    has_battery: bool,
    charge_percent: u8,
    drain_rate_mw: c_int,
    estimated_time_remaining_s: c_int,
};

// ── Exported functions ───────────────────────────────────────────────

export fn optisys_power_init() c_int {
    return 0; // Success
}

export fn optisys_power_snapshot(info: *CBatteryInfo) c_int {
    _ = info;
    // Will be implemented with Windows API calls via platform module
    return 0;
}

export fn optisys_power_source() c_int {
    return 0; // Unknown
}

export fn optisys_memory_init() c_int {
    return 0;
}

export fn optisys_memory_snapshot(info: *CMemoryInfo) c_int {
    _ = info;
    // Will be implemented with Windows API calls
    return 0;
}

export fn optisys_memory_optimize(level: c_int, excluded_count: c_int, excluded_pids: [*]c_int) c_int {
    _ = level;
    _ = excluded_count;
    _ = excluded_pids;
    // Will be implemented
    return 0;
}

export fn optisys_process_list(buffer: *CProcessInfo, buffer_size: c_int) c_int {
    _ = buffer;
    _ = buffer_size;
    // Will be implemented
    return 0;
}

export fn optisys_process_trim(pid: c_int) i64 {
    _ = pid;
    // Returns freed bytes
    return 0;
}

export fn optisys_set_eco_qos(pid: c_int, enable: bool) c_int {
    _ = pid;
    _ = enable;
    // Will be implemented via SetProcessInformation
    return 0;
}

export fn optisys_shutdown() void {
    // Cleanup any resources
}
