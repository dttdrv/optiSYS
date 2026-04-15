const std = @import("std");

const app_mod = @import("app.zig");
const config_mod = @import("../core/config.zig");
const engine_mod = @import("../core/engine.zig");
const telemetry_mod = @import("../core/telemetry.zig");
const types = @import("../core/types.zig");

pub const RuntimeError =
    app_mod.AppError ||
    config_mod.ConfigError ||
    telemetry_mod.TelemetryError ||
    engine_mod.EngineError ||
    error{
        InvalidState,
        OutOfMemory,
    };

pub const RuntimeState = enum {
    created,
    initialized,
    running,
    stopped,
};

pub const RuntimeOptions = struct {
    settings_path: []const u8 = "settings.json",
    snapshots_path: []const u8 = "snapshots.json",
    event_ring_capacity: usize = 256,
    engine_config: engine_mod.EngineConfig = .{},
    app_config: app_mod.AppConfig = .{
        .settings_path = "settings.json",
        .snapshots_path = "snapshots.json",
    },

    pub fn normalized(self: RuntimeOptions) RuntimeOptions {
        var out = self;
        if (out.event_ring_capacity == 0) {
            out.event_ring_capacity = 256;
        }

        out.app_config.settings_path = out.settings_path;
        out.app_config.snapshots_path = out.snapshots_path;
        return out;
    }
};

pub const RuntimeSnapshot = struct {
    state: RuntimeState,
    app_state: app_mod.RuntimeState,
    last_power_snapshot: ?types.BatteryInfo,
    last_memory_snapshot: ?types.MemoryInfo,
    event_count: usize,
    last_event: ?types.Event,
};

pub const Runtime = struct {
    allocator: std.mem.Allocator,
    options: RuntimeOptions,
    state: RuntimeState = .created,

    app: app_mod.App,
    engine: engine_mod.Engine,
    events: telemetry_mod.EventRing,

    last_power_snapshot: ?types.BatteryInfo = null,
    last_memory_snapshot: ?types.MemoryInfo = null,

    pub fn init(
        allocator: std.mem.Allocator,
        options: RuntimeOptions,
    ) RuntimeError!Runtime {
        const normalized = options.normalized();

        var event_ring = try telemetry_mod.EventRing.init(
            allocator,
            normalized.event_ring_capacity,
        );
        errdefer event_ring.deinit();

        var runtime = Runtime{
            .allocator = allocator,
            .options = normalized,
            .app = app_mod.App.init(allocator, normalized.app_config),
            .engine = engine_mod.Engine.init(allocator, normalized.engine_config),
            .events = event_ring,
        };

        runtime.app.setEventSink(.{
            .ctx = @ptrCast(&runtime),
            .handler = onAppEvent,
        });

        runtime.state = .initialized;
        return runtime;
    }

    pub fn deinit(self: *Runtime) void {
        if (self.state == .running) {
            self.stop() catch {};
        }

        if (self.state != .created) {
            self.app.deinit();
            self.events.deinit();
            self.state = .stopped;
        }
    }

    pub fn start(self: *Runtime) RuntimeError!void {
        switch (self.state) {
            .created => return error.InvalidState,
            .initialized, .stopped => {},
            .running => return error.InvalidState,
        }

        self.app.setEventSink(.{
            .ctx = @ptrCast(self),
            .handler = onAppEvent,
        });

        try self.app.start();
        self.state = .running;
    }

    pub fn stop(self: *Runtime) RuntimeError!void {
        if (self.state != .running) {
            return error.InvalidState;
        }

        try self.app.stop();
        self.state = .stopped;
    }

    pub fn tick(self: *Runtime) RuntimeError!void {
        if (self.state != .running) {
            return error.InvalidState;
        }

        try self.app.tick();
    }

    pub fn requestOptimization(
        self: *Runtime,
        reason: types.TriggerReason,
    ) RuntimeError!app_mod.Event {
        if (self.state != .running) {
            return error.InvalidState;
        }

        const decision = try self.app.requestOptimization(switch (reason) {
            .manual => .manual,
            .startup_recovery => .startup_recovery,
            .power_source_changed => .power_source_changed,
            .battery_discharge_trend => .battery_discharge_trend,
            .low_memory_notification => .low_memory_notification,
            .memory_threshold => .memory_threshold,
            .predictive_memory_threshold => .predictive_memory_threshold,
            .scheduled => .scheduled,
            .periodic_refresh => .periodic_refresh,
        });

        return .{ .optimization_completed = decision };
    }

    pub fn snapshot(self: *const Runtime) RuntimeSnapshot {
        return .{
            .state = self.state,
            .app_state = self.app.state,
            .last_power_snapshot = self.last_power_snapshot,
            .last_memory_snapshot = self.last_memory_snapshot,
            .event_count = self.events.len(),
            .last_event = self.events.newest(),
        };
    }

    pub fn eventSnapshot(
        self: *const Runtime,
        allocator: std.mem.Allocator,
    ) RuntimeError![]types.Event {
        return try self.events.snapshot(allocator);
    }

    pub fn filteredEventSnapshot(
        self: *const Runtime,
        allocator: std.mem.Allocator,
        filter: telemetry_mod.EventFilter,
    ) RuntimeError![]types.Event {
        return try self.events.filteredSnapshot(allocator, filter);
    }

    pub fn latestEvent(self: *const Runtime) ?types.Event {
        return self.events.newest();
    }

    pub fn eventStats(self: *const Runtime) telemetry_mod.EventStats {
        return self.events.stats;
    }

    fn handleEvent(self: *Runtime, event: app_mod.Event) void {
        switch (event) {
            .app_started => {
                self.pushEvent(.info, .system, null, "runtime started");
            },
            .app_stopped => {
                self.pushEvent(.info, .system, null, "runtime stopped");
            },
            .power_changed => |snapshot| {
                self.last_power_snapshot = .{
                    .has_battery = snapshot.has_battery,
                    .power_source = switch (snapshot.power_source) {
                        .unknown => .unknown,
                        .ac => .ac,
                        .battery => .battery,
                    },
                    .charge_state = switch (snapshot.charge_state) {
                        .unknown => .unknown,
                        .charging => .charging,
                        .discharging => .discharging,
                        .idle => .idle,
                    },
                    .charge_percent = snapshot.charge_percent,
                    .drain_rate_mw = snapshot.drain_rate_mw,
                    .estimated_time_remaining_s = snapshot.estimated_time_remaining_s,
                };
                self.pushEvent(.info, .power, .battery_monitor, "power snapshot updated");
            },
            .memory_pressure_changed => |snapshot| {
                self.last_memory_snapshot = .{
                    .total_physical_bytes = snapshot.total_physical_bytes,
                    .available_physical_bytes = snapshot.available_physical_bytes,
                    .cached_bytes = snapshot.cached_bytes,
                    .standby_bytes = snapshot.standby_bytes,
                    .free_bytes = snapshot.free_bytes,
                    .modified_bytes = snapshot.modified_bytes,
                    .compressed_bytes = snapshot.compressed_bytes,
                    .commit_total_bytes = snapshot.commit_total_bytes,
                    .commit_limit_bytes = snapshot.commit_limit_bytes,
                    .process_count = snapshot.process_count,
                    .thread_count = snapshot.thread_count,
                    .handle_count = snapshot.handle_count,
                    .low_memory = snapshot.low_memory,
                };
                self.pushEvent(.info, .memory, .memory_monitor, "memory snapshot updated");
            },
            .optimization_requested => |_| {
                self.pushEvent(.info, .policy, null, "optimization requested");
            },
            .optimization_completed => |decision| {
                const severity: types.Severity = if (decision.should_optimize) .info else .warn;
                self.pushEvent(severity, .policy, null, decision.reason_text);
            },
            .warning => |message| {
                self.pushEvent(.warn, .system, null, message);
            },
        }
    }

    fn pushEvent(
        self: *Runtime,
        severity: types.Severity,
        module_id: types.ModuleId,
        domain_id: ?types.DomainId,
        message: []const u8,
    ) void {
        self.events.push(telemetry_mod.makeEvent(
            std.time.milliTimestamp(),
            severity,
            module_id,
            domain_id,
            message,
        ));
    }

    fn onAppEvent(ctx: *anyopaque, event: app_mod.Event) void {
        const self: *Runtime = @ptrCast(@alignCast(ctx));
        self.handleEvent(event);
    }
};

test "runtime initializes with normalized options" {
    var runtime = try Runtime.init(std.testing.allocator, .{
        .settings_path = "a.json",
        .snapshots_path = "b.json",
        .event_ring_capacity = 8,
    });
    defer runtime.deinit();

    try std.testing.expectEqual(RuntimeState.initialized, runtime.state);
    try std.testing.expectEqual(@as(usize, 8), runtime.events.capacity());
    try std.testing.expectEqualStrings("a.json", runtime.options.app_config.settings_path);
    try std.testing.expectEqualStrings("b.json", runtime.options.app_config.snapshots_path);
}

test "runtime snapshot reflects initial state" {
    var runtime = try Runtime.init(std.testing.allocator, .{});
    defer runtime.deinit();

    const snap = runtime.snapshot();
    try std.testing.expectEqual(RuntimeState.initialized, snap.state);
    try std.testing.expectEqual(app_mod.RuntimeState.stopped, snap.app_state);
    try std.testing.expectEqual(@as(usize, 0), snap.event_count);
    try std.testing.expectEqual(@as(?types.Event, null), snap.last_event);
}
