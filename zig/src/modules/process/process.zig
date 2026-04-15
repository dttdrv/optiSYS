const std = @import("std");
const psapi = @import("../../platform/windows/psapi.zig");
const user32 = @import("../../platform/windows/user32.zig");

pub const ProcessError = error{
    OutOfMemory,
    InvalidArgument,
    SnapshotUnavailable,
} || psapi.Error;

pub const Config = struct {
    max_snapshot_count: usize = 128,
    min_working_set_bytes: u64 = 8 * 1024 * 1024,
    include_foreground_process: bool = true,

    pub fn validate(self: *Config) void {
        self.max_snapshot_count = std.math.clamp(self.max_snapshot_count, 1, 4096);
        self.min_working_set_bytes = std.math.clamp(self.min_working_set_bytes, 0, 1 << 40);
    }
};

pub const ProcessSnapshot = struct {
    pid: u32,
    name: []u8,
    working_set_bytes: u64,
    private_bytes: u64,
    pagefile_usage_bytes: u64,
    page_fault_count: u32,
    is_foreground: bool,
    is_excluded: bool = false,
    is_system_critical: bool = false,

    pub fn deinit(self: *ProcessSnapshot, allocator: std.mem.Allocator) void {
        allocator.free(self.name);
        self.* = undefined;
    }
};

pub const Snapshot = struct {
    items: []ProcessSnapshot,

    pub fn deinit(self: *Snapshot, allocator: std.mem.Allocator) void {
        for (self.items) |*item| {
            item.deinit(allocator);
        }
        allocator.free(self.items);
        self.* = undefined;
    }

    pub fn len(self: Snapshot) usize {
        return self.items.len;
    }

    pub fn isEmpty(self: Snapshot) bool {
        return self.items.len == 0;
    }
};

pub const ProcessModule = struct {
    allocator: std.mem.Allocator,
    config: Config,

    pub fn init(allocator: std.mem.Allocator, config: Config) ProcessError!ProcessModule {
        var validated = config;
        validated.validate();

        return .{
            .allocator = allocator,
            .config = validated,
        };
    }

    pub fn deinit(self: *ProcessModule, allocator: std.mem.Allocator) void {
        _ = allocator;
        self.* = undefined;
    }

    pub fn snapshot(self: *ProcessModule) ProcessError!Snapshot {
        const raw = try psapi.snapshotTopProcessesByWorkingSet(
            self.allocator,
            self.config.max_snapshot_count,
            self.config.min_working_set_bytes,
        );
        errdefer {
            for (raw) |*item| {
                item.deinit(self.allocator);
            }
            self.allocator.free(raw);
        }

        const foreground_pid = user32.getForegroundProcessId();
        var items = try self.allocator.alloc(ProcessSnapshot, raw.len);
        errdefer {
            for (items[0..]) |*item| {
                if (item.name.len != 0) item.deinit(self.allocator);
            }
            self.allocator.free(items);
        }

        var out_index: usize = 0;
        for (raw) |*item| {
            const is_foreground = foreground_pid != null and item.pid == foreground_pid.?;
            if (!self.config.include_foreground_process and is_foreground) {
                item.deinit(self.allocator);
                continue;
            }

            items[out_index] = .{
                .pid = item.pid,
                .name = item.image_name_utf8,
                .working_set_bytes = item.memory.working_set_bytes,
                .private_bytes = item.memory.private_usage_bytes,
                .pagefile_usage_bytes = item.memory.pagefile_usage_bytes,
                .page_fault_count = item.memory.page_fault_count,
                .is_foreground = is_foreground,
            };

            item.image_name_utf8 = &.{};
            out_index += 1;
        }

        self.allocator.free(raw);

        if (out_index != items.len) {
            items = try self.allocator.realloc(items, out_index);
        }

        return .{ .items = items };
    }

    pub fn poll(self: *ProcessModule) ProcessError!Snapshot {
        return self.snapshot();
    }

    pub fn topCandidate(self: *ProcessModule) ProcessError!?ProcessSnapshot {
        var snap = try self.snapshot();
        defer snap.deinit(self.allocator);

        if (snap.items.len == 0) return null;

        const first = snap.items[0];
        return .{
            .pid = first.pid,
            .name = try self.allocator.dupe(u8, first.name),
            .working_set_bytes = first.working_set_bytes,
            .private_bytes = first.private_bytes,
            .pagefile_usage_bytes = first.pagefile_usage_bytes,
            .page_fault_count = first.page_fault_count,
            .is_foreground = first.is_foreground,
            .is_excluded = first.is_excluded,
            .is_system_critical = first.is_system_critical,
        };
    }
};

test "config validation clamps values" {
    var config = Config{
        .max_snapshot_count = 0,
        .min_working_set_bytes = std.math.maxInt(u64),
    };
    config.validate();

    try std.testing.expectEqual(@as(usize, 1), config.max_snapshot_count);
    try std.testing.expect(config.min_working_set_bytes <= (1 << 40));
}

test "snapshot helpers report length and emptiness" {
    var empty = Snapshot{ .items = &.{} };

    try std.testing.expectEqual(@as(usize, 0), empty.len());
    try std.testing.expect(empty.isEmpty());
}

test "process snapshot deinit frees owned name" {
    var name = try std.testing.allocator.dupe(u8, "proc");
    var snapshot = ProcessSnapshot{
        .pid = 1,
        .name = name,
        .working_set_bytes = 10,
        .private_bytes = 20,
        .pagefile_usage_bytes = 30,
        .page_fault_count = 1,
        .is_foreground = false,
    };

    snapshot.deinit(std.testing.allocator);
}
