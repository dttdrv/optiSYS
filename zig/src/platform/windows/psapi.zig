 //! Minimal Windows PSAPI/process enumeration scaffold for the optiSYS Zig core.
 //!
 //! This module intentionally provides a small, typed subset of process-related
 //! Win32/PSAPI declarations and helper wrappers needed for:
 //! - process enumeration
 //! - image name lookup
 //! - working set / memory counters
 //! - lightweight candidate snapshotting
 //!
 //! Design goals:
 //! - keep ABI declarations explicit and auditable
 //! - avoid broad translated headers
 //! - provide Zig-friendly wrappers with explicit error handling
 //! - stay backend-only and host-agnostic

 const std = @import("std");
 const k32 = @import("kernel32.zig");

 pub const BOOL = k32.BOOL;
 pub const DWORD = k32.DWORD;
 pub const ULONG = k32.ULONG;
 pub const SIZE_T = usize;
 pub const HANDLE = k32.HANDLE;
 pub const WCHAR = k32.WCHAR;
 pub const LPVOID = k32.LPVOID;
 pub const LPCVOID = k32.LPCVOID;
 pub const PROCESS_QUERY_INFORMATION = k32.PROCESS_QUERY_INFORMATION;
 pub const PROCESS_QUERY_LIMITED_INFORMATION = k32.PROCESS_QUERY_LIMITED_INFORMATION;

 pub const MAX_PATH: usize = 260;

 pub const PROCESS_MEMORY_COUNTERS = extern struct {
     cb: DWORD,
     PageFaultCount: DWORD,
     PeakWorkingSetSize: SIZE_T,
     WorkingSetSize: SIZE_T,
     QuotaPeakPagedPoolUsage: SIZE_T,
     QuotaPagedPoolUsage: SIZE_T,
     QuotaPeakNonPagedPoolUsage: SIZE_T,
     QuotaNonPagedPoolUsage: SIZE_T,
     PagefileUsage: SIZE_T,
     PeakPagefileUsage: SIZE_T,
 };

 pub const PROCESS_MEMORY_COUNTERS_EX = extern struct {
     cb: DWORD,
     PageFaultCount: DWORD,
     PeakWorkingSetSize: SIZE_T,
     WorkingSetSize: SIZE_T,
     QuotaPeakPagedPoolUsage: SIZE_T,
     QuotaPagedPoolUsage: SIZE_T,
     QuotaPeakNonPagedPoolUsage: SIZE_T,
     QuotaNonPagedPoolUsage: SIZE_T,
     PagefileUsage: SIZE_T,
     PeakPagefileUsage: SIZE_T,
     PrivateUsage: SIZE_T,
 };

 pub const Error = error{
     AccessDenied,
     InvalidHandle,
     InvalidParameter,
     OutOfMemory,
     BufferTooSmall,
     Win32Failure,
     Unexpected,
 };

 pub extern "psapi" fn EnumProcesses(
     lpidProcess: [*]DWORD,
     cb: DWORD,
     lpcbNeeded: *DWORD,
 ) callconv(.winapi) BOOL;

 pub extern "psapi" fn GetProcessMemoryInfo(
     Process: HANDLE,
     ppsmemCounters: LPVOID,
     cb: DWORD,
 ) callconv(.winapi) BOOL;

 pub extern "kernel32" fn QueryFullProcessImageNameW(
     hProcess: HANDLE,
     dwFlags: DWORD,
     lpExeName: [*:0]WCHAR,
     lpdwSize: *DWORD,
 ) callconv(.winapi) BOOL;

 pub fn mapLastError(err: DWORD) Error {
     return switch (err) {
         5 => error.AccessDenied, // ERROR_ACCESS_DENIED
         6 => error.InvalidHandle, // ERROR_INVALID_HANDLE
         8 => error.OutOfMemory, // ERROR_NOT_ENOUGH_MEMORY
         87 => error.InvalidParameter, // ERROR_INVALID_PARAMETER
         122 => error.BufferTooSmall, // ERROR_INSUFFICIENT_BUFFER
         else => error.Win32Failure,
     };
 }

 fn checkBool(ok: BOOL) Error!void {
     if (k32.succeeded(ok)) return;
     return mapLastError(k32.lastError());
 }

 pub const ProcessMemoryInfo = struct {
     working_set_bytes: u64 = 0,
     peak_working_set_bytes: u64 = 0,
     pagefile_usage_bytes: u64 = 0,
     peak_pagefile_usage_bytes: u64 = 0,
     private_usage_bytes: u64 = 0,
     page_fault_count: u32 = 0,
 };

 pub const ProcessSnapshot = struct {
     pid: DWORD,
     image_name_utf8: []u8,
     memory: ProcessMemoryInfo,

     pub fn deinit(self: *ProcessSnapshot, allocator: std.mem.Allocator) void {
         allocator.free(self.image_name_utf8);
         self.* = undefined;
     }
 };

 pub fn enumProcessIds(allocator: std.mem.Allocator) Error![]DWORD {
     var capacity: usize = 256;

     while (true) {
         const buffer = try allocator.alloc(DWORD, capacity);
         errdefer allocator.free(buffer);

         var bytes_needed: DWORD = 0;
         try checkBool(EnumProcesses(
             buffer.ptr,
             @as(DWORD, @intCast(buffer.len * @sizeOf(DWORD))),
             &bytes_needed,
         ));

         const count: usize = @intCast(bytes_needed / @as(DWORD, @intCast(@sizeOf(DWORD))));
         if (count < buffer.len) {
             return allocator.realloc(buffer, count);
         }

         allocator.free(buffer);
         capacity *= 2;

         if (capacity > 1_048_576) {
             return error.Unexpected;
         }
     }
 }

 pub fn queryProcessMemoryInfo(handle: HANDLE) Error!ProcessMemoryInfo {
     var counters = PROCESS_MEMORY_COUNTERS_EX{
         .cb = @sizeOf(PROCESS_MEMORY_COUNTERS_EX),
         .PageFaultCount = 0,
         .PeakWorkingSetSize = 0,
         .WorkingSetSize = 0,
         .QuotaPeakPagedPoolUsage = 0,
         .QuotaPagedPoolUsage = 0,
         .QuotaPeakNonPagedPoolUsage = 0,
         .QuotaNonPagedPoolUsage = 0,
         .PagefileUsage = 0,
         .PeakPagefileUsage = 0,
         .PrivateUsage = 0,
     };

     try checkBool(GetProcessMemoryInfo(
         handle,
         @ptrCast(&counters),
         @sizeOf(PROCESS_MEMORY_COUNTERS_EX),
     ));

     return .{
         .working_set_bytes = counters.WorkingSetSize,
         .peak_working_set_bytes = counters.PeakWorkingSetSize,
         .pagefile_usage_bytes = counters.PagefileUsage,
         .peak_pagefile_usage_bytes = counters.PeakPagefileUsage,
         .private_usage_bytes = counters.PrivateUsage,
         .page_fault_count = counters.PageFaultCount,
     };
 }

 pub fn queryFullProcessImageNameUtf8(
     allocator: std.mem.Allocator,
     handle: HANDLE,
 ) Error![]u8 {
     var wide_buffer: [MAX_PATH]WCHAR = [_]WCHAR{0} ** MAX_PATH;
     var size_chars: DWORD = MAX_PATH - 1;

     try checkBool(QueryFullProcessImageNameW(
         handle,
         0,
         @ptrCast(&wide_buffer),
         &size_chars,
     ));

     const wide_slice = wide_buffer[0..@as(usize, @intCast(size_chars))];
     return std.unicode.utf16LeToUtf8Alloc(allocator, wide_slice) catch error.Win32Failure;
 }

 pub fn openProcessForQuery(pid: DWORD) Error!k32.OwnedHandle {
     const handle = k32.OpenProcess(
         PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_QUERY_INFORMATION,
         k32.FALSE,
         pid,
     );

     if (handle == null) {
         return mapLastError(k32.lastError());
     }

     return k32.OwnedHandle.init(handle);
 }

 pub fn snapshotProcess(
     allocator: std.mem.Allocator,
     pid: DWORD,
 ) Error!ProcessSnapshot {
     var handle = try openProcessForQuery(pid);
     defer handle.deinit();

     const image_name = try queryFullProcessImageNameUtf8(allocator, handle.handle);
     errdefer allocator.free(image_name);

     const memory = try queryProcessMemoryInfo(handle.handle);

     return .{
         .pid = pid,
         .image_name_utf8 = image_name,
         .memory = memory,
     };
 }

 pub fn snapshotTopProcessesByWorkingSet(
     allocator: std.mem.Allocator,
     max_count: usize,
     min_working_set_bytes: u64,
 ) Error![]ProcessSnapshot {
     const pids = try enumProcessIds(allocator);
     defer allocator.free(pids);

     var snapshots = std.ArrayList(ProcessSnapshot).empty;
     defer {
         for (snapshots.items) |*item| {
             item.deinit(allocator);
         }
         snapshots.deinit(allocator);
     }

     for (pids) |pid| {
         if (pid == 0) continue;

         const snap = snapshotProcess(allocator, pid) catch |err| switch (err) {
             error.AccessDenied,
             error.InvalidHandle,
             error.Win32Failure,
             error.BufferTooSmall,
             => continue,
             else => return err,
         };

         if (snap.memory.working_set_bytes < min_working_set_bytes) {
             var tmp = snap;
             tmp.deinit(allocator);
             continue;
         }

         try snapshots.append(allocator, snap);
     }

     std.mem.sort(ProcessSnapshot, snapshots.items, {}, struct {
         fn lessThan(_: void, a: ProcessSnapshot, b: ProcessSnapshot) bool {
             return a.memory.working_set_bytes > b.memory.working_set_bytes;
         }
     }.lessThan);

     if (snapshots.items.len > max_count) {
         var i = max_count;
         while (i < snapshots.items.len) : (i += 1) {
             snapshots.items[i].deinit(allocator);
         }
         try snapshots.resize(allocator, max_count);
     }

     return snapshots.toOwnedSlice(allocator);
 }

 test "mapLastError maps common Win32 values" {
     try std.testing.expectEqual(Error.AccessDenied, mapLastError(5));
     try std.testing.expectEqual(Error.InvalidHandle, mapLastError(6));
     try std.testing.expectEqual(Error.InvalidParameter, mapLastError(87));
     try std.testing.expectEqual(Error.BufferTooSmall, mapLastError(122));
 }

 test "process memory info default values are zeroed" {
     const info = ProcessMemoryInfo{};
     try std.testing.expectEqual(@as(u64, 0), info.working_set_bytes);
     try std.testing.expectEqual(@as(u64, 0), info.private_usage_bytes);
     try std.testing.expectEqual(@as(u32, 0), info.page_fault_count);
 }
