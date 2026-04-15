const std = @import("std");
const core_types = @import("../../core/types.zig");

pub const PolicyError = error{
    InvalidState,
    InvalidArgument,
    OutOfMemory,
};

pub const OptimizationReason = core_types.TriggerReason;
pub const OptimizationLevel = core_types.OptimizationLevel;
pub const PowerSource = core_types.PowerSource;

pub const OptimizationRequest = struct {
    reason: OptimizationReason = .manual,
    power: ?PowerSnapshot = null,
    memory: ?MemorySnapshot = null,
};

pub const OptimizationDecisionTag = enum {
    no_action,
    optimize_memory,
    optimize_power,
    optimize_power_and_memory,
};

pub const OptimizationDecision = struct {
    tag: OptimizationDecisionTag = .no_action,
    level: OptimizationLevel = .balanced,
    reason: OptimizationReason = .manual,
    message: []const u8 = "no action",
};

pub const PowerSnapshot = struct {
    source: PowerSource = .unknown,
    charge_percent: u8 = 0,
    drain_rate_mw: i32 = 0,
};

pub const MemorySnapshot = struct {
    usage_percent: f32 = 0,
    low_memory: bool = false,
};

pub const PolicyConfig = struct {
    memory_threshold_percent: u8 = 80,
    battery_low_percent: u8 = 25,
    enable_predictive_optimization: bool = true,

    pub fn validate(self: *PolicyConfig) void {
        self.memory_threshold_percent = std.math.clamp(self.memory_threshold_percent, 10, 95);
        self.battery_low_percent = std.math.clamp(self.battery_low_percent, 5, 80);
    }
};

pub const PolicyEngine = struct {
    allocator: std.mem.Allocator,
    config: PolicyConfig,

    pub fn init(allocator: std.mem.Allocator, config: PolicyConfig) PolicyError!PolicyEngine {
        var validated = config;
        validated.validate();

        return .{
            .allocator = allocator,
            .config = validated,
        };
    }

    pub fn deinit(self: *PolicyEngine, allocator: std.mem.Allocator) void {
        _ = allocator;
        _ = self;
    }

    pub fn evaluate(self: *PolicyEngine, request: OptimizationRequest) PolicyError!OptimizationDecision {
        _ = self.allocator;

        if (request.memory) |memory| {
            if (memory.low_memory or memory.usage_percent >= @as(f32, @floatFromInt(self.config.memory_threshold_percent))) {
                return .{
                    .tag = .optimize_memory,
                    .level = .balanced,
                    .reason = request.reason,
                    .message = "memory pressure detected",
                };
            }
        }

        if (request.power) |power| {
            if (power.source == .battery and power.charge_percent <= self.config.battery_low_percent) {
                return .{
                    .tag = .optimize_power,
                    .level = .balanced,
                    .reason = request.reason,
                    .message = "battery conservation recommended",
                };
            }
        }

        if (request.power != null and request.memory != null) {
            const power = request.power.?;
            const memory = request.memory.?;

            if (power.source == .battery and memory.usage_percent >= 70) {
                return .{
                    .tag = .optimize_power_and_memory,
                    .level = .balanced,
                    .reason = request.reason,
                    .message = "combined battery and memory optimization recommended",
                };
            }
        }

        return .{
            .tag = .no_action,
            .level = .balanced,
            .reason = request.reason,
            .message = "conditions do not require optimization",
        };
    }
};

test "policy engine recommends memory optimization under pressure" {
    var engine = try PolicyEngine.init(std.testing.allocator, .{});
    defer engine.deinit(std.testing.allocator);

    const decision = try engine.evaluate(.{
        .reason = .memory_threshold,
        .memory = .{
            .usage_percent = 85,
            .low_memory = false,
        },
    });

    try std.testing.expectEqual(OptimizationDecisionTag.optimize_memory, decision.tag);
}

test "policy engine recommends power optimization on low battery" {
    var engine = try PolicyEngine.init(std.testing.allocator, .{});
    defer engine.deinit(std.testing.allocator);

    const decision = try engine.evaluate(.{
        .reason = .power_source_changed,
        .power = .{
            .source = .battery,
            .charge_percent = 20,
            .drain_rate_mw = -12000,
        },
    });

    try std.testing.expectEqual(OptimizationDecisionTag.optimize_power, decision.tag);
}

test "policy engine returns no action when conditions are normal" {
    var engine = try PolicyEngine.init(std.testing.allocator, .{});
    defer engine.deinit(std.testing.allocator);

    const decision = try engine.evaluate(.{
        .reason = .manual,
        .power = .{
            .source = .ac,
            .charge_percent = 90,
            .drain_rate_mw = 0,
        },
        .memory = .{
            .usage_percent = 45,
            .low_memory = false,
        },
    });

    try std.testing.expectEqual(OptimizationDecisionTag.no_action, decision.tag);
}
