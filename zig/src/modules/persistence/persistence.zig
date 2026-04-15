const std = @import("std");
const config_mod = @import("../../core/config.zig");

pub const PersistenceError = error{
    OutOfMemory,
    InvalidArgument,
    IoFailure,
    NotFound,
    CorruptData,
    SerializationFailure,
    DeserializationFailure,
    AtomicWriteFailed,
};

pub const StoreConfig = struct {
    settings_path: []const u8,
    snapshots_path: []const u8,
};

pub const SnapshotRecord = struct {
    key: []u8,
    value: []u8,

    pub fn deinit(self: *SnapshotRecord, allocator: std.mem.Allocator) void {
        allocator.free(self.key);
        allocator.free(self.value);
        self.* = undefined;
    }
};

pub const SnapshotStore = struct {
    allocator: std.mem.Allocator,
    path: []u8,
    records: std.StringHashMapUnmanaged([]u8) = .empty,

    pub fn init(allocator: std.mem.Allocator, path: []const u8) PersistenceError!SnapshotStore {
        return .{
            .allocator = allocator,
            .path = allocator.dupe(u8, path) catch return error.OutOfMemory,
        };
    }

    pub fn deinit(self: *SnapshotStore) void {
        var it = self.records.iterator();
        while (it.next()) |entry| {
            self.allocator.free(entry.key_ptr.*);
            self.allocator.free(entry.value_ptr.*);
        }
        self.records.deinit(self.allocator);
        self.allocator.free(self.path);
        self.* = undefined;
    }

    pub fn put(self: *SnapshotStore, key: []const u8, value: []const u8) PersistenceError!void {
        const owned_key = self.allocator.dupe(u8, key) catch return error.OutOfMemory;
        errdefer self.allocator.free(owned_key);

        const owned_value = self.allocator.dupe(u8, value) catch return error.OutOfMemory;
        errdefer self.allocator.free(owned_value);

        const gop = self.records.getOrPut(self.allocator, owned_key) catch return error.OutOfMemory;
        if (gop.found_existing) {
            self.allocator.free(owned_key);
            self.allocator.free(gop.value_ptr.*);
            gop.value_ptr.* = owned_value;
            return;
        }

        gop.value_ptr.* = owned_value;
    }

    pub fn get(self: *const SnapshotStore, key: []const u8) ?[]const u8 {
        return self.records.get(key);
    }

    pub fn remove(self: *SnapshotStore, key: []const u8) bool {
        const removed = self.records.fetchRemove(key) orelse return false;
        self.allocator.free(removed.key);
        self.allocator.free(removed.value);
        return true;
    }

    pub fn clear(self: *SnapshotStore) void {
        var it = self.records.iterator();
        while (it.next()) |entry| {
            self.allocator.free(entry.key_ptr.*);
            self.allocator.free(entry.value_ptr.*);
        }
        self.records.clearRetainingCapacity();
    }

    pub fn count(self: *const SnapshotStore) usize {
        return self.records.count();
    }

    pub fn loadFromDisk(self: *SnapshotStore) PersistenceError!void {
        self.clear();

        const file = std.fs.cwd().openFile(self.path, .{}) catch |err| switch (err) {
            error.FileNotFound => return,
            else => return mapFsError(err),
        };
        defer file.close();

        const bytes = file.readToEndAlloc(self.allocator, 16 * 1024 * 1024) catch return error.OutOfMemory;
        defer self.allocator.free(bytes);

        if (bytes.len == 0) return;

        var parsed = std.json.parseFromSlice(std.json.Value, self.allocator, bytes, .{
            .ignore_unknown_fields = true,
            .allocate = .alloc_always,
        }) catch return error.DeserializationFailure;
        defer parsed.deinit();

        if (parsed.value != .object) return error.CorruptData;

        var it = parsed.value.object.iterator();
        while (it.next()) |entry| {
            if (entry.value_ptr.* != .string) continue;
            try self.put(entry.key_ptr.*, entry.value_ptr.*.string);
        }
    }

    pub fn saveToDisk(self: *const SnapshotStore) PersistenceError!void {
        const dir_path = std.fs.path.dirname(self.path) orelse ".";
        std.fs.cwd().makePath(dir_path) catch |err| switch (err) {
            else => return mapFsError(err),
        };

        var buffer = std.ArrayList(u8).init(self.allocator);
        defer buffer.deinit();

        try writeSnapshotJson(self, buffer.writer());

        const tmp_path = try std.fmt.allocPrint(self.allocator, "{s}.tmp", .{self.path});
        defer self.allocator.free(tmp_path);

        {
            const tmp = std.fs.cwd().createFile(tmp_path, .{ .truncate = true }) catch |err| return mapFsError(err);
            defer tmp.close();

            tmp.writeAll(buffer.items) catch |err| return mapFsError(err);
        }

        std.fs.cwd().rename(tmp_path, self.path) catch |err| {
            std.fs.cwd().deleteFile(tmp_path) catch {};
            return mapFsError(err);
        };
    }
};

pub const Store = struct {
    allocator: std.mem.Allocator,
    settings_path: []u8,
    snapshots: SnapshotStore,

    pub fn init(allocator: std.mem.Allocator, cfg: StoreConfig) PersistenceError!Store {
        var snapshots = try SnapshotStore.init(allocator, cfg.snapshots_path);
        errdefer snapshots.deinit();

        return .{
            .allocator = allocator,
            .settings_path = allocator.dupe(u8, cfg.settings_path) catch return error.OutOfMemory,
            .snapshots = snapshots,
        };
    }

    pub fn deinit(self: *Store, allocator: std.mem.Allocator) void {
        _ = allocator;
        self.snapshots.deinit();
        self.allocator.free(self.settings_path);
        self.* = undefined;
    }

    pub fn loadConfig(self: *Store) PersistenceError!config_mod.Config {
        const file = std.fs.cwd().openFile(self.settings_path, .{}) catch |err| switch (err) {
            error.FileNotFound => return config_mod.Config.initDefaults(self.allocator) catch return error.OutOfMemory,
            else => return mapFsError(err),
        };
        defer file.close();

        const bytes = file.readToEndAlloc(self.allocator, 16 * 1024 * 1024) catch return error.OutOfMemory;
        defer self.allocator.free(bytes);

        return config_mod.parseJson(self.allocator, bytes) catch |err| switch (err) {
            error.OutOfMemory => error.OutOfMemory,
            else => error.DeserializationFailure,
        };
    }

    pub fn saveConfig(self: *Store, cfg: *const config_mod.Config) PersistenceError!void {
        const dir_path = std.fs.path.dirname(self.settings_path) orelse ".";
        std.fs.cwd().makePath(dir_path) catch |err| switch (err) {
            else => return mapFsError(err),
        };

        const json = cfg.toJsonAlloc(self.allocator) catch |err| switch (err) {
            error.OutOfMemory => return error.OutOfMemory,
            else => return error.SerializationFailure,
        };
        defer self.allocator.free(json);

        const tmp_path = try std.fmt.allocPrint(self.allocator, "{s}.tmp", .{self.settings_path});
        defer self.allocator.free(tmp_path);

        {
            const tmp = std.fs.cwd().createFile(tmp_path, .{ .truncate = true }) catch |err| return mapFsError(err);
            defer tmp.close();

            tmp.writeAll(json) catch |err| return mapFsError(err);
        }

        std.fs.cwd().rename(tmp_path, self.settings_path) catch |err| {
            std.fs.cwd().deleteFile(tmp_path) catch {};
            return mapFsError(err);
        };
    }

    pub fn loadSnapshots(self: *Store) PersistenceError!void {
        try self.snapshots.loadFromDisk();
    }

    pub fn saveSnapshots(self: *Store) PersistenceError!void {
        try self.snapshots.saveToDisk();
    }

    pub fn putSnapshot(self: *Store, key: []const u8, value: []const u8) PersistenceError!void {
        try self.snapshots.put(key, value);
    }

    pub fn getSnapshot(self: *const Store, key: []const u8) ?[]const u8 {
        return self.snapshots.get(key);
    }

    pub fn removeSnapshot(self: *Store, key: []const u8) bool {
        return self.snapshots.remove(key);
    }

    pub fn clearSnapshots(self: *Store) void {
        self.snapshots.clear();
    }
};

fn writeSnapshotJson(store: *const SnapshotStore, writer: anytype) PersistenceError!void {
    try writer.writeAll("{\n");

    var first = true;
    var it = store.records.iterator();
    while (it.next()) |entry| {
        if (!first) try writer.writeAll(",\n");
        first = false;

        try writer.writeAll("  ");
        std.json.stringify(entry.key_ptr.*, .{}, writer) catch return error.SerializationFailure;
        try writer.writeAll(": ");
        std.json.stringify(entry.value_ptr.*, .{}, writer) catch return error.SerializationFailure;
    }

    try writer.writeAll("\n}\n");
}

fn mapFsError(err: anyerror) PersistenceError {
    return switch (err) {
        error.FileNotFound => error.NotFound,
        error.AccessDenied => error.IoFailure,
        error.PathAlreadyExists => error.IoFailure,
        error.NameTooLong => error.InvalidArgument,
        error.NoSpaceLeft => error.IoFailure,
        error.NotDir => error.InvalidArgument,
        error.IsDir => error.InvalidArgument,
        else => error.IoFailure,
    };
}

test "snapshot store put get remove works" {
    var store = try SnapshotStore.init(std.testing.allocator, "snapshots.json");
    defer store.deinit();

    try store.put("alpha", "one");
    try std.testing.expectEqual(@as(usize, 1), store.count());
    try std.testing.expectEqualStrings("one", store.get("alpha").?);

    try store.put("alpha", "two");
    try std.testing.expectEqual(@as(usize, 1), store.count());
    try std.testing.expectEqualStrings("two", store.get("alpha").?);

    try std.testing.expect(store.remove("alpha"));
    try std.testing.expect(store.get("alpha") == null);
}

test "store init wires paths" {
    var store = try Store.init(std.testing.allocator, .{
        .settings_path = "settings.json",
        .snapshots_path = "snapshots.json",
    });
    defer store.deinit(std.testing.allocator);

    try std.testing.expectEqualStrings("settings.json", store.settings_path);
    try std.testing.expectEqualStrings("snapshots.json", store.snapshots.path);
}
