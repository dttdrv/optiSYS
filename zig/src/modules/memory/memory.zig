const std = @import("std");
const types = @import("../../core/types.zig");
const telemetry = @import("../../core/telemetry.zig");

pub const MemoryError = error{
    AlreadyStarted,
    NotStarted,
    InvalidConfiguration,
    OutOfMemory,
    PlatformUnavailable,
    SampleUnavailable,
};

pub const PressureLevel = enum {
    normal,
    elevated,
    high,
    critical,
};

pub const TriggerKind = enum {
    none,
    low_memory_notification,
    threshold,
    predictive_threshold,
    scheduled,
};

pub const Config = struct {
    check_interval_seconds: u32 = 5,
    threshold_percent: u8 = 80,
    hysteresis_gap: u8 = 10,
    cooldown_seconds: u32 = 30,
    trend_window_size: u32 = 10,
    predictive_lead_seconds: u32 = 15,
    accessed_bits_delay_ms: u32 = 2000,
    effectiveness_tracking_enabled: bool = true,
    scheduled_optimize_enabled: bool = false,
    scheduled_optimize_interval_minutes: u32 = 30,
    self_working_set_cap_mb: u32 = 25,
    cache_max_percent: u8 = 0,

    pub fn validate(self: *Config) void {
        self.check_interval_seconds = clampInt(u32, self.check_interval_seconds, 1, 60, 5);
        self.threshold_percent = clampInt(u8, self.threshold_percent, 10, 95, 80);
        self.hysteresis_gap = clampInt(u8, self.hysteresis_gap, 1, 30, 10);
        self.cooldown_seconds = clampInt(u32, self.cooldown_seconds, 5, 300, 30);
        self.trend_window_size = clampInt(u32, self.trend_window_size, 3, 60, 10);
        self.predictive_lead_seconds = clampInt(u32, self.predictive_lead_seconds, 5, 120, 15);
        self.accessed_bits_delay_ms = clampInt(u32, self.accessed_bits_delay_ms, 0, 5000, 2000);
        self.scheduled_optimize_interval_minutes = clampInt(u32, self.scheduled_optimize_interval_minutes, 1, 1440, 30);
        self.self_working_set_cap_mb = clampInt(u32, self.self_working_set_cap_mb, 0, 256, 25);
        self.cache_max_percent = clampInt(u8, self.cache_max_percent, 0, 75, 0);
    }
};

pub const MemorySnapshot = struct {
    info: types.MemoryInfo,
    pressure_level: PressureLevel,
    trigger_kind: TriggerKind = .none,
    timestamp_ms: types.TimestampMs,

    pub fn usagePercent(self: MemorySnapshot) f32 {
        return self.info.usagePercent();
    }

    pub fn isLowMemory(self: MemorySnapshot) bool {
        return self.info.low_memory or self.pressure_level == .critical;
    }
};

pub const UsageSample = struct {
    timestamp_ms: types.TimestampMs,
    usage_percent: f32,
};

pub const TriggerDecision = struct {
    should_optimize: bool = false,
    trigger_kind: TriggerKind = .none,
    predicted_usage_percent: f32 = 0,
    reason: []const u8 = "",
};

pub const MemoryProvider = struct {
    ctx: *anyopaque,
    poll_fn: *const fn (ctx: *anyopaque) anyerror!types.MemoryInfo,
};

pub const Clock = struct {
    ctx: *anyopaque,
    now_ms_fn: *const fn (ctx: *anyopaque) types.TimestampMs,
};

pub const MemoryModule = struct {
    allocator: std.mem.Allocator,
    config: Config,
    provider: MemoryProvider,
    clock: Clock,

    started: bool = false,
    last_snapshot: ?MemorySnapshot = null,
    last_optimization_at_ms: ?types.TimestampMs = null,
    last_self_trim_at_ms: ?types.TimestampMs = null,
    auto_optimize_armed: bool = true,

    usage_history: std.ArrayList(UsageSample),
    event_ring: telemetry.EventRing,
    effectiveness: telemetry.EffectivenessTracker,

    pub fn init(
        allocator: std.mem.Allocator,
        config: Config,
        provider: MemoryProvider,
        clock: Clock,
    ) MemoryError!MemoryModule {
        var validated = config;
        validated.validate();

        return .{
            .allocator = allocator,
            .config = validated,
            .provider = provider,
            .clock = clock,
            .usage_history = std.ArrayList(UsageSample).empty,
            .event_ring = telemetry.EventRing.init(allocator, 128) catch return error.OutOfMemory,
            .effectiveness = telemetry.EffectivenessTracker.init(allocator),
        };
    }

    pub fn deinit(self: *MemoryModule, allocator: std.mem.Allocator) void {
        _ = allocator;
        self.usage_history.deinit(self.allocator);
        self.event_ring.deinit();
        self.effectiveness.deinit();
        self.* = undefined;
    }

    pub fn start(self: *MemoryModule) MemoryError!void {
        if (self.started) return error.AlreadyStarted;
        self.started = true;
        self.auto_optimize_armed = true;
        self.pushEvent(.info, "memory module started");
    }

    pub fn stop(self: *MemoryModule) MemoryError!void {
        if (!self.started) return error.NotStarted;
        self.started = false;
        self.pushEvent(.info, "memory module stopped");
    }

    pub fn poll(self: *MemoryModule) MemoryError!MemorySnapshot {
        if (!self.started) return error.NotStarted;

        const now_ms = self.nowMs();
        const info = self.provider.poll_fn(self.provider.ctx) catch return error.SampleUnavailable;

        const snapshot = MemorySnapshot{
            .info = info,
            .pressure_level = classifyPressure(info, self.config.threshold_percent),
            .timestamp_ms = now_ms,
        };

        self.last_snapshot = snapshot;
        self.recordUsage(snapshot) catch return error.OutOfMemory;
        self.updateHysteresis(snapshot.usagePercent());

        return snapshot;
    }

    pub fn evaluateTrigger(self: *MemoryModule) MemoryError!TriggerDecision {
        if (!self.started) return error.NotStarted;

        const snapshot = self.last_snapshot orelse return .{
            .should_optimize = false,
            .reason = "no memory snapshot available",
        };

        if (!self.isCooldownSatisfied(snapshot.timestamp_ms)) {
            return .{
                .should_optimize = false,
                .reason = "cooldown active",
            };
        }

        if (snapshot.isLowMemory() and self.auto_optimize_armed) {
            self.auto_optimize_armed = false;
            return .{
                .should_optimize = true,
                .trigger_kind = .low_memory_notification,
                .reason = "low memory condition reported by provider",
            };
        }

        if (self.auto_optimize_armed and snapshot.usagePercent() >= @as(f32, @floatFromInt(self.config.threshold_percent))) {
            self.auto_optimize_armed = false;
            return .{
                .should_optimize = true,
                .trigger_kind = .threshold,
                .predicted_usage_percent = snapshot.usagePercent(),
                .reason = "usage threshold reached",
            };
        }

        if (self.auto_optimize_armed) {
            const predicted = self.predictUsagePercent(self.config.predictive_lead_seconds);
            if (predicted >= @as(f32, @floatFromInt(self.config.threshold_percent))) {
                self.auto_optimize_armed = false;
                return .{
                    .should_optimize = true,
                    .trigger_kind = .predictive_threshold,
                    .predicted_usage_percent = predicted,
                    .reason = "predicted threshold breach",
                };
            }
        }

        if (self.config.scheduled_optimize_enabled and self.isScheduledOptimizationDue(snapshot.timestamp_ms)) {
            return .{
                .should_optimize = true,
                .trigger_kind = .scheduled,
                .reason = "scheduled optimization interval elapsed",
            };
        }

        return .{
            .should_optimize = false,
            .reason = "no trigger matched",
        };
    }

    pub fn markOptimizationCompleted(
        self: *MemoryModule,
        trigger_kind: TriggerKind,
        before_available_bytes: u64,
        after_available_bytes: u64,
        duration_ms: u32,
        confidence: f32,
    ) MemoryError!void {
        if (!self.started) return error.NotStarted;

        const snapshot = self.last_snapshot orelse return error.SampleUnavailable;
        self.last_optimization_at_ms = snapshot.timestamp_ms;

        self.effectiveness.record(.{
            .timestamp_ms = snapshot.timestamp_ms,
            .domain_id = .memory_optimizer,
            .trigger_reason = mapTriggerKind(trigger_kind),
            .duration_ms = duration_ms,
            .before_available_bytes = before_available_bytes,
            .after_available_bytes = after_available_bytes,
            .confidence = confidence,
        }) catch return error.OutOfMemory;

        self.pushEvent(.info, "memory optimization completed");
    }

    pub fn shouldTrimSelf(self: *MemoryModule) bool {
        const snapshot = self.last_snapshot orelse return false;
        const now_ms = snapshot.timestamp_ms;

        if (self.config.self_working_set_cap_mb == 0) return false;
        if (snapshot.usagePercent() < @as(f32, @floatFromInt(self.config.threshold_percent))) return false;

        if (self.last_self_trim_at_ms) |last_ms| {
            const min_interval_ms = @as(i64, self.config.cooldown_seconds) * std.time.ms_per_s;
            if (now_ms - last_ms < min_interval_ms) return false;
        }

        return true;
    }

    pub fn markSelfTrim(self: *MemoryModule) void {
        const snapshot = self.last_snapshot orelse return;
        self.last_self_trim_at_ms = snapshot.timestamp_ms;
        self.pushEvent(.debug, "self trim recorded");
    }

    pub fn latestSnapshot(self: *const MemoryModule) ?MemorySnapshot {
        return self.last_snapshot;
    }

    pub fn summarizeEffectiveness(self: *const MemoryModule) telemetry.EffectivenessSummary {
        return self.effectiveness.summarizeDomain(.memory_optimizer);
    }

    pub fn events(self: *const MemoryModule) *const telemetry.EventRing {
        return &self.event_ring;
    }

    fn nowMs(self: *const MemoryModule) types.TimestampMs {
        return self.clock.now_ms_fn(self.clock.ctx);
    }

    fn recordUsage(self: *MemoryModule, snapshot: MemorySnapshot) !void {
        try self.usage_history.append(self.allocator, .{
            .timestamp_ms = snapshot.timestamp_ms,
            .usage_percent = snapshot.usagePercent(),
        });

        while (self.usage_history.items.len > self.config.trend_window_size) {
            _ = self.usage_history.orderedRemove(0);
        }
    }

    fn updateHysteresis(self: *MemoryModule, usage_percent: f32) void {
        const threshold = @as(f32, @floatFromInt(self.config.threshold_percent));
        const rearm_threshold = threshold - @as(f32, @floatFromInt(self.config.hysteresis_gap));

        if (usage_percent >= threshold) {
            self.auto_optimize_armed = true;
            return;
        }

        if (usage_percent <= rearm_threshold) {
            self.auto_optimize_armed = true;
        }
    }

    fn isCooldownSatisfied(self: *const MemoryModule, now_ms: types.TimestampMs) bool {
        const last_ms = self.last_optimization_at_ms orelse return true;
        const cooldown_ms = @as(i64, self.config.cooldown_seconds) * std.time.ms_per_s;
        return now_ms - last_ms >= cooldown_ms;
    }

    fn isScheduledOptimizationDue(self: *const MemoryModule, now_ms: types.TimestampMs) bool {
        const last_ms = self.last_optimization_at_ms orelse return true;
        const interval_ms = @as(i64, self.config.scheduled_optimize_interval_minutes) * std.time.ms_per_min;
        return now_ms - last_ms >= interval_ms;
    }

    fn predictUsagePercent(self: *const MemoryModule, lead_seconds: u32) f32 {
        if (self.usage_history.items.len < 3) return 0;

        const first = self.usage_history.items[0];
        var sum_t: f64 = 0;
        var sum_u: f64 = 0;
        var sum_tu: f64 = 0;
        var sum_t2: f64 = 0;

        for (self.usage_history.items) |sample| {
            const t = @as(f64, @floatFromInt(sample.timestamp_ms - first.timestamp_ms)) / @as(f64, std.time.ms_per_s);
            const u = @as(f64, sample.usage_percent);
            sum_t += t;
            sum_u += u;
            sum_tu += t * u;
            sum_t2 += t * t;
        }

        const n = @as(f64, @floatFromInt(self.usage_history.items.len));
        const denominator = (n * sum_t2) - (sum_t * sum_t);
        if (@abs(denominator) < 0.0001) return 0;

        const slope = ((n * sum_tu) - (sum_t * sum_u)) / denominator;
        const current = self.usage_history.items[self.usage_history.items.len - 1].usage_percent;
        const predicted = @as(f64, current) + (slope * @as(f64, @floatFromInt(lead_seconds)));

        return @floatCast(std.math.clamp(predicted, 0.0, 100.0));
    }

    fn pushEvent(self: *MemoryModule, severity: types.Severity, message: []const u8) void {
        self.event_ring.push(telemetry.makeEvent(
            self.nowMs(),
            severity,
            .memory,
            .memory_monitor,
            message,
        ));
    }
};

pub fn classifyPressure(info: types.MemoryInfo, threshold_percent: u8) PressureLevel {
    const usage = info.usagePercent();
    const threshold = @as(f32, @floatFromInt(threshold_percent));

    if (info.low_memory or usage >= threshold + 10.0) return .critical;
    if (usage >= threshold) return .high;
    if (usage >= threshold - 10.0) return .elevated;
    return .normal;
}

fn mapTriggerKind(kind: TriggerKind) types.TriggerReason {
    return switch (kind) {
        .none => .periodic_refresh,
        .low_memory_notification => .low_memory_notification,
        .threshold => .memory_threshold,
        .predictive_threshold => .predictive_memory_threshold,
        .scheduled => .scheduled,
    };
}

fn clampInt(comptime T: type, value: T, min: T, max: T, fallback: T) T {
    if (min > max) return fallback;
    return std.math.clamp(value, min, max);
}

const TestClock = struct {
    now_ms: types.TimestampMs,

    fn now(ctx: *anyopaque) types.TimestampMs {
        const self: *TestClock = @ptrCast(@alignCast(ctx));
        return self.now_ms;
    }
};

const TestProvider = struct {
    info: types.MemoryInfo,

    fn poll(ctx: *anyopaque) anyerror!types.MemoryInfo {
        const self: *TestProvider = @ptrCast(@alignCast(ctx));
        return self.info;
    }
};

test "memory module polls and classifies pressure" {
    var clock = TestClock{ .now_ms = 1000 };
    var provider = TestProvider{
        .info = .{
            .total_physical_bytes = 100,
            .available_physical_bytes = 15,
            .low_memory = false,
        },
    };

    var module = try MemoryModule.init(
        std.testing.allocator,
        .{},
        .{
            .ctx = &provider,
            .poll_fn = TestProvider.poll,
        },
        .{
            .ctx = &clock,
            .now_ms_fn = TestClock.now,
        },
    );
    defer module.deinit(std.testing.allocator);

    try module.start();
    const snapshot = try module.poll();

    try std.testing.expectEqual(PressureLevel.high, snapshot.pressure_level);
    try std.testing.expectApproxEqAbs(@as(f32, 85.0), snapshot.usagePercent(), 0.001);
}

test "memory module threshold trigger arms and fires" {
    var clock = TestClock{ .now_ms = 1000 };
    var provider = TestProvider{
        .info = .{
            .total_physical_bytes = 100,
            .available_physical_bytes = 10,
            .low_memory = false,
        },
    };

    var module = try MemoryModule.init(
        std.testing.allocator,
        .{},
        .{
            .ctx = &provider,
            .poll_fn = TestProvider.poll,
        },
        .{
            .ctx = &clock,
            .now_ms_fn = TestClock.now,
        },
    );
    defer module.deinit(std.testing.allocator);

    try module.start();
    _ = try module.poll();

    const decision = try module.evaluateTrigger();
    try std.testing.expect(decision.should_optimize);
    try std.testing.expectEqual(TriggerKind.threshold, decision.trigger_kind);
}

test "memory module cooldown suppresses repeated trigger" {
    var clock = TestClock{ .now_ms = 1000 };
    var provider = TestProvider{
        .info = .{
            .total_physical_bytes = 100,
            .available_physical_bytes = 10,
            .low_memory = false,
        },
    };

    var module = try MemoryModule.init(
        std.testing.allocator,
        .{},
        .{
            .ctx = &provider,
            .poll_fn = TestProvider.poll,
        },
        .{
            .ctx = &clock,
            .now_ms_fn = TestClock.now,
        },
    );
    defer module.deinit(std.testing.allocator);

    try module.start();
    _ = try module.poll();
    _ = try module.evaluateTrigger();
    try module.markOptimizationCompleted(.threshold, 10, 20, 5, 0.8);

    clock.now_ms = 2000;
    _ = try module.poll();
    const decision = try module.evaluateTrigger();

    try std.testing.expect(!decision.should_optimize);
}

test "predictive trigger estimates upward trend" {
    var clock = TestClock{ .now_ms = 1000 };
    var provider = TestProvider{
        .info = .{
            .total_physical_bytes = 100,
            .available_physical_bytes = 30,
            .low_memory = false,
        },
    };

    var module = try MemoryModule.init(
        std.testing.allocator,
        .{
            .threshold_percent = 80,
            .trend_window_size = 5,
            .predictive_lead_seconds = 15,
        },
        .{
            .ctx = &provider,
            .poll_fn = TestProvider.poll,
        },
        .{
            .ctx = &clock,
            .now_ms_fn = TestClock.now,
        },
    );
    defer module.deinit(std.testing.allocator);

    try module.start();

    provider.info.available_physical_bytes = 40;
    clock.now_ms = 1000;
    _ = try module.poll();

    provider.info.available_physical_bytes = 30;
    clock.now_ms = 6000;
    _ = try module.poll();

    provider.info.available_physical_bytes = 20;
    clock.now_ms = 11000;
    _ = try module.poll();

    const decision = try module.evaluateTrigger();
    try std.testing.expect(decision.should_optimize);
    try std.testing.expectEqual(TriggerKind.predictive_threshold, decision.trigger_kind);
}

test "self trim only allowed under pressure and after interval" {
    var clock = TestClock{ .now_ms = 1000 };
    var provider = TestProvider{
        .info = .{
            .total_physical_bytes = 100,
            .available_physical_bytes = 10,
            .low_memory = false,
        },
    };

    var module = try MemoryModule.init(
        std.testing.allocator,
        .{
            .self_working_set_cap_mb = 25,
            .threshold_percent = 80,
            .cooldown_seconds = 30,
        },
        .{
            .ctx = &provider,
            .poll_fn = TestProvider.poll,
        },
        .{
            .ctx = &clock,
            .now_ms_fn = TestClock.now,
        },
    );
    defer module.deinit(std.testing.allocator);

    try module.start();
    _ = try module.poll();

    try std.testing.expect(module.shouldTrimSelf());
    module.markSelfTrim();
    try std.testing.expect(!module.shouldTrimSelf());

    clock.now_ms = 40 * std.time.ms_per_s;
    _ = try module.poll();
    try std.testing.expect(module.shouldTrimSelf());
}
