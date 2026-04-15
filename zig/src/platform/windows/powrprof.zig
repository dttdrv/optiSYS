 //! Minimal Windows `powrprof` interop scaffold for the optiSYS Zig core.
 //!
 //! This module provides a small, typed subset of the Windows power-management
 //! APIs needed for the first backend-only milestones:
 //! - reading the active power scheme
 //! - reading and writing AC/DC power values
 //! - activating a power scheme
 //! - reading detailed battery state
 //!
 //! Design goals:
 //! - keep the ABI surface explicit and auditable
 //! - avoid broad translated headers early in the migration
 //! - provide Zig-friendly wrappers with explicit error handling
 //! - keep policy logic out of the platform layer

 const std = @import("std");
 const kernel32 = @import("kernel32.zig");

 pub const DWORD = u32;
 pub const ULONG = u32;
 pub const HANDLE = kernel32.HANDLE;
 pub const GUID = kernel32.GUID;

 pub const SYSTEM_BATTERY_STATE = extern struct {
     AcOnLine: u8,
     BatteryPresent: u8,
     Charging: u8,
     Discharging: u8,
     Spare1: [4]u8,
     MaxCapacity: ULONG,
     RemainingCapacity: ULONG,
     Rate: i32,
     EstimatedTime: ULONG,
     DefaultAlert1: ULONG,
     DefaultAlert2: ULONG,
 };

 pub const Error = error{
     AccessDenied,
     InvalidParameter,
     NotSupported,
     OutOfMemory,
     BufferTooSmall,
     Unexpected,
     Win32Failure,
     PowrProfFailure,
 };

 pub const SYSTEM_POWER_INFORMATION_LEVEL = enum(c_int) {
     SystemBatteryState = 5,
 };

 pub extern "powrprof" fn CallNtPowerInformation(
     InformationLevel: SYSTEM_POWER_INFORMATION_LEVEL,
     InputBuffer: ?*const anyopaque,
     InputBufferLength: ULONG,
     OutputBuffer: ?*anyopaque,
     OutputBufferLength: ULONG,
 ) callconv(.winapi) ULONG;

 pub extern "powrprof" fn PowerGetActiveScheme(
     UserRootPowerKey: ?*const anyopaque,
     ActivePolicyGuid: *?*GUID,
 ) callconv(.winapi) DWORD;

 pub extern "powrprof" fn PowerReadACValueIndex(
     RootPowerKey: ?*const anyopaque,
     SchemeGuid: *const GUID,
     SubGroupOfPowerSettingsGuid: *const GUID,
     PowerSettingGuid: *const GUID,
     AcValueIndex: *DWORD,
 ) callconv(.winapi) DWORD;

 pub extern "powrprof" fn PowerReadDCValueIndex(
     RootPowerKey: ?*const anyopaque,
     SchemeGuid: *const GUID,
     SubGroupOfPowerSettingsGuid: *const GUID,
     PowerSettingGuid: *const GUID,
     DcValueIndex: *DWORD,
 ) callconv(.winapi) DWORD;

 pub extern "powrprof" fn PowerWriteACValueIndex(
     RootPowerKey: ?*const anyopaque,
     SchemeGuid: *const GUID,
     SubGroupOfPowerSettingsGuid: *const GUID,
     PowerSettingGuid: *const GUID,
     AcValueIndex: DWORD,
 ) callconv(.winapi) DWORD;

 pub extern "powrprof" fn PowerWriteDCValueIndex(
     RootPowerKey: ?*const anyopaque,
     SchemeGuid: *const GUID,
     SubGroupOfPowerSettingsGuid: *const GUID,
     PowerSettingGuid: *const GUID,
     DcValueIndex: DWORD,
 ) callconv(.winapi) DWORD;

 pub extern "powrprof" fn PowerSetActiveScheme(
     UserRootPowerKey: ?*const anyopaque,
     SchemeGuid: *const GUID,
 ) callconv(.winapi) DWORD;

 pub extern "kernel32" fn LocalFree(hMem: ?*anyopaque) callconv(.winapi) ?*anyopaque;

 pub const ERROR_SUCCESS: DWORD = 0;
 pub const ERROR_ACCESS_DENIED: DWORD = 5;
 pub const ERROR_NOT_ENOUGH_MEMORY: DWORD = 8;
 pub const ERROR_INVALID_PARAMETER: DWORD = 87;
 pub const ERROR_INSUFFICIENT_BUFFER: DWORD = 122;
 pub const ERROR_NOT_SUPPORTED: DWORD = 50;

 pub const GUID_PROCESSOR_SETTINGS_SUBGROUP = GUID{
     .Data1 = 0x54533251,
     .Data2 = 0x82BE,
     .Data3 = 0x4824,
     .Data4 = .{ 0x96, 0xC1, 0x47, 0xB6, 0x0B, 0x74, 0x0D, 0x00 },
 };

 pub const GUID_PROCESSOR_THROTTLE_MINIMUM = GUID{
     .Data1 = 0x893DEE8E,
     .Data2 = 0x2BEF,
     .Data3 = 0x41E0,
     .Data4 = .{ 0x89, 0xC6, 0xB5, 0x5D, 0x09, 0x29, 0x96, 0x4C },
 };

 pub const GUID_PROCESSOR_THROTTLE_MAXIMUM = GUID{
     .Data1 = 0xBC5038F7,
     .Data2 = 0x23E0,
     .Data3 = 0x4960,
     .Data4 = .{ 0x96, 0xDA, 0x33, 0xAB, 0xAF, 0x59, 0x35, 0xEC },
 };

 pub const GUID_PROCESSOR_PARKING_CORE_THRESHOLD = GUID{
     .Data1 = 0x0CC5B647,
     .Data2 = 0xC1DF,
     .Data3 = 0x4637,
     .Data4 = .{ 0x89, 0x1A, 0xDE, 0xC3, 0x5C, 0x31, 0x85, 0x83 },
 };

 pub const GUID_DISK_SUBGROUP = GUID{
     .Data1 = 0x0012EE47,
     .Data2 = 0x9041,
     .Data3 = 0x4B5D,
     .Data4 = .{ 0x9B, 0x77, 0x53, 0x5F, 0xBA, 0x8B, 0x14, 0x42 },
 };

 pub const GUID_DISK_IDLE_TIMEOUT = GUID{
     .Data1 = 0x6738E2C4,
     .Data2 = 0xE8A5,
     .Data3 = 0x4A42,
     .Data4 = .{ 0xB1, 0x6A, 0xE0, 0x40, 0xE7, 0x69, 0x75, 0x6E },
 };

 pub const GUID_DISK_AHCI_LINK_POWER = GUID{
     .Data1 = 0xDAB60367,
     .Data2 = 0x53FE,
     .Data3 = 0x4FBC,
     .Data4 = .{ 0x82, 0x5E, 0x52, 0x1D, 0x06, 0x9D, 0x24, 0x56 },
 };

 pub fn mapWin32Error(code: DWORD) Error {
     return switch (code) {
         ERROR_SUCCESS => unreachable,
         ERROR_ACCESS_DENIED => error.AccessDenied,
         ERROR_NOT_ENOUGH_MEMORY => error.OutOfMemory,
         ERROR_INVALID_PARAMETER => error.InvalidParameter,
         ERROR_INSUFFICIENT_BUFFER => error.BufferTooSmall,
         ERROR_NOT_SUPPORTED => error.NotSupported,
         else => error.Win32Failure,
     };
 }

 fn checkWin32(code: DWORD) Error!void {
     if (code == ERROR_SUCCESS) return;
     return mapWin32Error(code);
 }

 pub const OwnedGuid = struct {
     ptr: ?*GUID = null,

     pub fn init(ptr: ?*GUID) OwnedGuid {
         return .{ .ptr = ptr };
     }

     pub fn isValid(self: OwnedGuid) bool {
         return self.ptr != null;
     }

     pub fn value(self: OwnedGuid) Error!GUID {
         if (self.ptr) |p| return p.*;
         return error.InvalidParameter;
     }

     pub fn deinit(self: *OwnedGuid) void {
         if (self.ptr) |p| {
             _ = LocalFree(@ptrCast(p));
         }
         self.ptr = null;
     }
 };

 pub fn getActiveScheme() Error!OwnedGuid {
     var guid_ptr: ?*GUID = null;
     try checkWin32(PowerGetActiveScheme(null, &guid_ptr));
     return OwnedGuid.init(guid_ptr);
 }

 pub fn readAcValueIndex(
     scheme_guid: *const GUID,
     subgroup_guid: *const GUID,
     setting_guid: *const GUID,
 ) Error!DWORD {
     var value: DWORD = 0;
     try checkWin32(PowerReadACValueIndex(
         null,
         scheme_guid,
         subgroup_guid,
         setting_guid,
         &value,
     ));
     return value;
 }

 pub fn readDcValueIndex(
     scheme_guid: *const GUID,
     subgroup_guid: *const GUID,
     setting_guid: *const GUID,
 ) Error!DWORD {
     var value: DWORD = 0;
     try checkWin32(PowerReadDCValueIndex(
         null,
         scheme_guid,
         subgroup_guid,
         setting_guid,
         &value,
     ));
     return value;
 }

 pub fn writeAcValueIndex(
     scheme_guid: *const GUID,
     subgroup_guid: *const GUID,
     setting_guid: *const GUID,
     value: DWORD,
 ) Error!void {
     try checkWin32(PowerWriteACValueIndex(
         null,
         scheme_guid,
         subgroup_guid,
         setting_guid,
         value,
     ));
 }

 pub fn writeDcValueIndex(
     scheme_guid: *const GUID,
     subgroup_guid: *const GUID,
     setting_guid: *const GUID,
     value: DWORD,
 ) Error!void {
     try checkWin32(PowerWriteDCValueIndex(
         null,
         scheme_guid,
         subgroup_guid,
         setting_guid,
         value,
     ));
 }

 pub fn setActiveScheme(scheme_guid: *const GUID) Error!void {
     try checkWin32(PowerSetActiveScheme(null, scheme_guid));
 }

 pub fn getBatteryState() Error!SYSTEM_BATTERY_STATE {
     var state: SYSTEM_BATTERY_STATE = std.mem.zeroes(SYSTEM_BATTERY_STATE);

     const status = CallNtPowerInformation(
         .SystemBatteryState,
         null,
         0,
         @ptrCast(&state),
         @sizeOf(SYSTEM_BATTERY_STATE),
     );

     if (status == 0) {
         return state;
     }

     return error.PowrProfFailure;
 }

 pub const BatterySnapshot = struct {
     ac_online: bool,
     battery_present: bool,
     charging: bool,
     discharging: bool,
     max_capacity_mwh: u32,
     remaining_capacity_mwh: u32,
     rate_mw: i32,
     estimated_time_s: ?u32,

     pub fn chargePercent(self: BatterySnapshot) u8 {
         if (self.max_capacity_mwh == 0) return 0;
         const percent = (@as(f64, @floatFromInt(self.remaining_capacity_mwh)) * 100.0) /
             @as(f64, @floatFromInt(self.max_capacity_mwh));
         const clamped = std.math.clamp(percent, 0.0, 100.0);
         return @intFromFloat(clamped);
     }

     pub fn watts(self: BatterySnapshot) f64 {
         const abs_rate: i32 = if (self.rate_mw < 0) -self.rate_mw else self.rate_mw;
         return @as(f64, @floatFromInt(abs_rate)) / 1000.0;
     }
 };

 pub fn getBatterySnapshot() Error!BatterySnapshot {
     const state = try getBatteryState();
     return .{
         .ac_online = state.AcOnLine != 0,
         .battery_present = state.BatteryPresent != 0,
         .charging = state.Charging != 0,
         .discharging = state.Discharging != 0,
         .max_capacity_mwh = state.MaxCapacity,
         .remaining_capacity_mwh = state.RemainingCapacity,
         .rate_mw = state.Rate,
         .estimated_time_s = if (state.EstimatedTime == 0 or state.EstimatedTime == 0xFFFFFFFF) null else state.EstimatedTime,
     };
 }

 pub const SchemeValuePair = struct {
     ac: DWORD,
     dc: DWORD,
 };

 pub fn readAcDcValuePair(
     scheme_guid: *const GUID,
     subgroup_guid: *const GUID,
     setting_guid: *const GUID,
 ) Error!SchemeValuePair {
     return .{
         .ac = try readAcValueIndex(scheme_guid, subgroup_guid, setting_guid),
         .dc = try readDcValueIndex(scheme_guid, subgroup_guid, setting_guid),
     };
 }

 pub fn writeAcDcValuePair(
     scheme_guid: *const GUID,
     subgroup_guid: *const GUID,
     setting_guid: *const GUID,
     pair: SchemeValuePair,
 ) Error!void {
     try writeAcValueIndex(scheme_guid, subgroup_guid, setting_guid, pair.ac);
     errdefer writeAcValueIndex(scheme_guid, subgroup_guid, setting_guid, pair.ac) catch {};

     try writeDcValueIndex(scheme_guid, subgroup_guid, setting_guid, pair.dc);
 }

 pub fn readCurrentSchemeAcDcValuePair(
     subgroup_guid: *const GUID,
     setting_guid: *const GUID,
 ) Error!SchemeValuePair {
     var active = try getActiveScheme();
     defer active.deinit();

     const scheme = try active.value();
     return try readAcDcValuePair(&scheme, subgroup_guid, setting_guid);
 }

 pub fn writeCurrentSchemeAcDcValuePair(
     subgroup_guid: *const GUID,
     setting_guid: *const GUID,
     pair: SchemeValuePair,
 ) Error!void {
     var active = try getActiveScheme();
     defer active.deinit();

     const scheme = try active.value();
     try writeAcDcValuePair(&scheme, subgroup_guid, setting_guid, pair);
     try setActiveScheme(&scheme);
 }

 pub fn readCurrentSchemeDcValue(
     subgroup_guid: *const GUID,
     setting_guid: *const GUID,
 ) Error!DWORD {
     var active = try getActiveScheme();
     defer active.deinit();

     const scheme = try active.value();
     return try readDcValueIndex(&scheme, subgroup_guid, setting_guid);
 }

 pub fn writeCurrentSchemeDcValue(
     subgroup_guid: *const GUID,
     setting_guid: *const GUID,
     value: DWORD,
 ) Error!void {
     var active = try getActiveScheme();
     defer active.deinit();

     const scheme = try active.value();
     try writeDcValueIndex(&scheme, subgroup_guid, setting_guid, value);
     try setActiveScheme(&scheme);
 }

 pub fn readCurrentSchemeAcValue(
     subgroup_guid: *const GUID,
     setting_guid: *const GUID,
 ) Error!DWORD {
     var active = try getActiveScheme();
     defer active.deinit();

     const scheme = try active.value();
     return try readAcValueIndex(&scheme, subgroup_guid, setting_guid);
 }

 pub fn writeCurrentSchemeAcValue(
     subgroup_guid: *const GUID,
     setting_guid: *const GUID,
     value: DWORD,
 ) Error!void {
     var active = try getActiveScheme();
     defer active.deinit();

     const scheme = try active.value();
     try writeAcValueIndex(&scheme, subgroup_guid, setting_guid, value);
     try setActiveScheme(&scheme);
 }

 test "owned guid invalid by default" {
     var guid = OwnedGuid{};
     defer guid.deinit();
     try std.testing.expect(!guid.isValid());
 }

 test "battery snapshot charge percent handles zero capacity" {
     const snapshot = BatterySnapshot{
         .ac_online = false,
         .battery_present = true,
         .charging = false,
         .discharging = true,
         .max_capacity_mwh = 0,
         .remaining_capacity_mwh = 5000,
         .rate_mw = -12000,
         .estimated_time_s = null,
     };

     try std.testing.expectEqual(@as(u8, 0), snapshot.chargePercent());
 }

 test "battery snapshot watts uses absolute rate" {
     const snapshot = BatterySnapshot{
         .ac_online = false,
         .battery_present = true,
         .charging = false,
         .discharging = true,
         .max_capacity_mwh = 60000,
         .remaining_capacity_mwh = 30000,
         .rate_mw = -18500,
         .estimated_time_s = 3600,
     };

     try std.testing.expectApproxEqAbs(@as(f64, 18.5), snapshot.watts(), 0.0001);
 }

 test "scheme value pair stores ac and dc values" {
     const pair = SchemeValuePair{
         .ac = 100,
         .dc = 25,
     };

     try std.testing.expectEqual(@as(DWORD, 100), pair.ac);
     try std.testing.expectEqual(@as(DWORD, 25), pair.dc);
 }
