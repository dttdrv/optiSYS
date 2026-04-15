const std = @import("std");
const k32 = @import("../../platform/windows/kernel32.zig");

pub const PowerError = error{
    AlreadyStarted,
    NotStarted,
    OutOfMemory,
    PlatformFailure,
} || k32.Error;

pub const PowerSource = enum {
    unknown,
    ac,
    battery,
};

pub const ChargeState = enum {
    unknown,
    charging,
    discharging,
    idle,
};

pub const TriggerReason = enum {
    startup,
    poll,
    power_event,
    manual_refresh,
};

pub const Config = struct {
    debounce_seconds: u32 = 2,
    poll_interval_on_ac_seconds: u32 = 15,
    poll_interval_on_battery_seconds: u32 = 5,
    savings_average_window_seconds: u32 = 60,

    pub fn validate(self: *Config) void {
        self.debounce_seconds = std.math.clamp(self.debounce_seconds, 1, 10);
        self.poll_interval_on_ac_seconds = std.math.clamp(self.poll_interval_on_ac_seconds, 5, 300);
        self.poll_interval_on_battery_seconds = std.math.clamp(self.poll_interval_on_battery_seconds, 2, 120);
        self.savings_average_window_seconds = std.math.clamp(self.savings_average_window_seconds, 10, 600);
    }
};

pub const PowerSnapshot = struct {
    timestamp_ms: i64,
    source: PowerSource,
    charge_state: ChargeState,
    has_battery: bool,
    charge_percent: u8,
    drain_rate_mw: i32,
    estimated_time_remaining_s: ?u32,
    trigger_reason: TriggerReason,

    pub fn watts(self: PowerSnapshot) f64 {
        const abs_rate: i32 = if (self.drain_rate_mw < 0) -self.drain_rate_mw else self.drain_rate_mw;
        return @as(f64, @floatFromInt(abs_rate)) / 1000.0;
    }

    pub fn isOnAc(self: PowerSnapshot) bool {
        return self.source == .ac;
    }

    pub fn isOnBattery(self: PowerSnapshot) bool {
        return self.source == .battery;
    }
};

pub const EventTag = enum {
    started,
    stopped,
    snapshot,
    power_source_changed,
    warning,
};

pub const Event = union(EventTag) {
    started: void,
    stopped: void,
    snapshot: PowerSnapshot,
    power_source_changed: PowerSnapshot,
    warning: []const u8,
};

pub const EventHandler = *const fn (ctx: *anyopaque, event: Event) void;

pub const EventSink = struct {
    ctx: *anyopaque,
    handler: EventHandler,

    pub fn emit(self: EventSink, event: Event) void {
        self.handler(self.ctx, event);
    }
};

pub const RollingAverage = struct {
    allocator: std.mem.Allocator,
    values: []f64,
    next_index: usize = 0,
    count: usize = 0,
    sum: f64 = 0,

    pub fn init(allocator: std.mem.Allocator, capacity: usize) !RollingAverage {
        if (capacity == 0) return error.OutOfMemory;

        const values = try allocator.alloc(f64, capacity);
        @memset(values, 0);

        return .{
            .allocator = allocator,
            .values = values,
        };
    }

    pub fn deinit(self: *RollingAverage) void {
        self.allocator.free(self.values);
        self.* = undefined;
    }

    pub fn push(self: *RollingAverage, value: f64) void {
        if (self.count < self.values.len) {
            self.values[self.next_index] = value;
            self.sum += value;
            self.count += 1;
            self.next_index = (self.next_index + 1) % self.values.len;
            return;
        }

        self.sum -= self.values[self.next_index];
        self.values[self.next_index] = value;
        self.sum += value;
        self.next_index = (self.next_index + 1) % self.values.len;
    }

    pub fn average(self: RollingAverage) f64 {
        if (self.count == 0) return 0;
        return self.sum / @as(f64, @floatFromInt(self.count));
    }

    pub fn clear(self: *RollingAverage) void {
        @memset(self.values, 0);
        self.next_index = 0;
        self.count = 0;
        self.sum = 0;
    }
};

pub const PowerModule = struct {
    allocator: std.mem.Allocator,
    config: Config,
    started: bool = false,
    event_sink: ?EventSink = null,
    last_snapshot: ?PowerSnapshot = null,
    pending_source: ?PowerSource = null,
    pending_since_ms: ?i64 = null,
    savings_window: RollingAverage,

    pub fn init(allocator: std.mem.Allocator, config: Config) !PowerModule {
        var validated = config;
        validated.validate();

        const window_capacity = @max(@as(usize, 1), @as(usize, @intCast(validated.savings_average_window_seconds)));
        return .{
            .allocator = allocator,
            .config = validated,
            .savings_window = try RollingAverage.init(allocator, window_capacity),
        };
    }

    pub fn deinit(self: *PowerModule, allocator: std.mem.Allocator) void {
        _ = allocator;
        self.savings_window.deinit();
        self.* = undefined;
    }

    pub fn setEventSink(self: *PowerModule, sink: EventSink) void {
        self.event_sink = sink;
    }

    pub fn start(self: *PowerModule) PowerError!void {
        if (self.started) return error.AlreadyStarted;

        self.started = true;
        self.emit(.{ .started = {} });

        const snapshot = try self.readSnapshot(.startup);
        self.last_snapshot = snapshot;
        self.savings_window.push(snapshot.watts());
        self.emit(.{ .snapshot = snapshot });
    }

    pub fn stop(self: *PowerModule) PowerError!void {
        if (!self.started) return error.NotStarted;

        self.started = false;
        self.pending_source = null;
        self.pending_since_ms = null;
        self.emit(.{ .stopped = {} });
    }

    pub fn poll(self: *PowerModule) PowerError!PowerSnapshot {
        if (!self.started) return error.NotStarted;

        const snapshot = try self.readSnapshot(.poll);
        self.handleSnapshot(snapshot);
        return snapshot;
    }

    pub fn refresh(self: *PowerModule) PowerError!PowerSnapshot {
        if (!self.started) return error.NotStarted;

        const snapshot = try self.readSnapshot(.manual_refresh);
        self.handleSnapshot(snapshot);
        return snapshot;
    }

    pub fn currentPollIntervalSeconds(self: PowerModule) u32 {
        const snapshot = self.last_snapshot orelse return self.config.poll_interval_on_ac_seconds;
        return if (snapshot.source == .battery)
            self.config.poll_interval_on_battery_seconds
        else
            self.config.poll_interval_on_ac_seconds;
    }

    pub fn averageWatts(self: PowerModule) f64 {
        return self.savings_window.average();
    }

    fn handleSnapshot(self: *PowerModule, snapshot: PowerSnapshot) void {
        self.savings_window.push(snapshot.watts());
        self.emit(.{ .snapshot = snapshot });

        if (self.last_snapshot) |previous| {
            if (snapshot.source != previous.source) {
                self.handlePotentialTransition(snapshot);
            } else {
                self.pending_source = null;
                self.pending_since_ms = null;
            }
        }

        self.last_snapshot = snapshot;
    }

    fn handlePotentialTransition(self: *PowerModule, snapshot: PowerSnapshot) void {
        const now_ms = snapshot.timestamp_ms;

        if (self.pending_source == null or self.pending_source.? != snapshot.source) {
            self.pending_source = snapshot.source;
            self.pending_since_ms = now_ms;
            return;
        }

        const pending_since_ms = self.pending_since_ms orelse now_ms;
        const debounce_ms = @as(i64, self.config.debounce_seconds) * std.time.ms_per_s;

        if (now_ms - pending_since_ms >= debounce_ms) {
            self.pending_source = null;
            self.pending_since_ms = null;
            self.emit(.{ .power_source_changed = snapshot });
        }
    }

    fn readSnapshot(self: *PowerModule, reason: TriggerReason) PowerError!PowerSnapshot {
        _ = self;

        const status = try k32.getSystemPowerStatus();

        const source: PowerSource = switch (status.ACLineStatus) {
            0 => .battery,
            1 => .ac,
            else => .unknown,
        };

        const has_battery = status.BatteryFlag != 128;
        const charge_state = deriveChargeState(source, status.BatteryFlag);
        const charge_percent: u8 = if (status.BatteryLifePercent <= 100) status.BatteryLifePercent else 0;
        const estimated_time_remaining_s: ?u32 = if (status.BatteryLifeTime == 0 or status.BatteryLifeTime == 0xFFFFFFFF)
            null
        else
            status.BatteryLifeTime;

        return .{
            .timestamp_ms = std.time.milliTimestamp(),
            .source = source,
            .charge_state = charge_state,
            .has_battery = has_battery,
            .charge_percent = charge_percent,
            .drain_rate_mw = 0,
            .estimated_time_remaining_s = estimated_time_remaining_s,
            .trigger_reason = reason,
        };
    }

    fn emit(self: *PowerModule, event: Event) void {
        if (self.event_sink) |sink| {
            sink.emit(event);
        }
    }
};

fn deriveChargeState(source: PowerSource, battery_flag: u8) ChargeState {
    if (source == .ac) {
        if ((battery_flag & 8) != 0) return .charging;
        return .idle;
    }

    if (source == .battery) return .discharging;
    return .unknown;
}

test "config validation clamps values" {
    var config = Config{
        .debounce_seconds = 0,
        .poll_interval_on_ac_seconds = 1,
        .poll_interval_on_battery_seconds = 999,
        .savings_average_window_seconds = 0,
    };

    config.validate();

    try std.testing.expectEqual(@as(u32, 1), config.debounce_seconds);
    try std.testing.expectEqual(@as(u32, 5), config.poll_interval_on_ac_seconds);
    try std.testing.expectEqual(@as(u32, 120), config.poll_interval_on_battery_seconds);
    try std.testing.expectEqual(@as(u32, 10), config.savings_average_window_seconds);
}

test "rolling average computes expected value" {
    var avg = try RollingAverage.init(std.testing.allocator, 3);
    defer avg.deinit();

    avg.push(10);
    avg.push(20);
    avg.push(30);
    try std.testing.expectApproxEqAbs(@as(f64, 20), avg.average(), 0.0001);

    avg.push(40);
    try std.testing.expectApproxEqAbs(@as(f64, 30), avg.average(), 0.0001);
}

test "snapshot watts uses absolute drain rate" {
    const snapshot = PowerSnapshot{
        .timestamp_ms = 0,
        .source = .battery,
        .charge_state = .discharging,
        .has_battery = true,
        .charge_percent = 50,
        .drain_rate_mw = -12500,
        .estimated_time_remaining_s = null,
        .trigger_reason = .poll,
    };

    try std.testing.expectApproxEqAbs(@as(f64, 12.5), snapshot.watts(), 0.0001);
}

test "power module initializes stopped" {
    var module = try PowerModule.init(std.testing.allocator, .{});
    defer module.deinit(std.testing.allocator);

    try std.testing.expect(!module.started);
    try std.testing.expectEqual(@as(?PowerSnapshot, null), module.last_snapshot);
}

test "derive charge state matches source and flags" {
    try std.testing.expectEqual(ChargeState.charging, deriveChargeState(.ac, 8));
    try std.testing.expectEqual(ChargeState.idle, deriveChargeState(.ac, 0));
    try std.testing.expectEqual(ChargeState.discharging, deriveChargeState(.battery, 0));
    try std.testing.expectEqual(ChargeState.unknown, deriveChargeState(.unknown, 0));
}
