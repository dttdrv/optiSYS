const std = @import("std");

const power = @import("../modules/power/power.zig");
const memory = @import("../modules/memory/memory.zig");
const process = @import("../modules/process/process.zig");
const policy = @import("../modules/policy/policy.zig");
const persistence = @import("../modules/persistence/persistence.zig");

pub const AppError = error{
    AlreadyStarted,
    NotStarted,
} || power.PowerError || memory.MemoryError || process.ProcessError || policy.PolicyError || persistence.PersistenceError;

pub const AppConfig = struct {
    settings_path: []const u8,
    snapshots_path: []const u8,
    enable_power_module: bool = true,
    enable_memory_module: bool = true,
    enable_process_module: bool = true,
    enable_policy_engine: bool = true,
};

pub const RuntimeState = enum {
    stopped,
    starting,
    running,
    stopping,
};

pub const EventTag = enum {
    app_started,
    app_stopped,
    power_changed,
    memory_pressure_changed,
    optimization_requested,
    optimization_completed,
    warning,
};

pub const Event = union(EventTag) {
    app_started: void,
    app_stopped: void,
    power_changed: power.PowerSnapshot,
    memory_pressure_changed: memory.MemorySnapshot,
    optimization_requested: policy.OptimizationRequest,
    optimization_completed: policy.OptimizationDecision,
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

pub const App = struct {
    allocator: std.mem.Allocator,
    config: AppConfig,
    state: RuntimeState = .stopped,

    power_module: ?power.PowerModule = null,
    memory_module: ?memory.MemoryModule = null,
    process_module: ?process.ProcessModule = null,
    policy_engine: ?policy.PolicyEngine = null,
    store: ?persistence.Store = null,

    event_sink: ?EventSink = null,
    last_power_snapshot: ?power.PowerSnapshot = null,
    last_memory_snapshot: ?memory.MemorySnapshot = null,

    pub fn init(allocator: std.mem.Allocator, config: AppConfig) App {
        return .{
            .allocator = allocator,
            .config = config,
        };
    }

    pub fn deinit(self: *App) void {
        self.stop() catch {};
    }

    pub fn setEventSink(self: *App, sink: EventSink) void {
        self.event_sink = sink;
    }

    pub fn start(self: *App) AppError!void {
        if (self.state != .stopped) return error.AlreadyStarted;

        self.state = .starting;
        errdefer self.state = .stopped;

        self.store = try persistence.Store.init(self.allocator, .{
            .settings_path = self.config.settings_path,
            .snapshots_path = self.config.snapshots_path,
        });
        errdefer {
            if (self.store) |*store| store.deinit(self.allocator);
            self.store = null;
        }

        if (self.config.enable_power_module) {
            self.power_module = try power.PowerModule.init(self.allocator, .{});
            errdefer {
                if (self.power_module) |*module| module.deinit(self.allocator);
                self.power_module = null;
            }
        }

        if (self.config.enable_memory_module) {
            self.memory_module = try memory.MemoryModule.init(self.allocator, .{});
            errdefer {
                if (self.memory_module) |*module| module.deinit(self.allocator);
                self.memory_module = null;
            }
        }

        if (self.config.enable_process_module) {
            self.process_module = try process.ProcessModule.init(self.allocator, .{});
            errdefer {
                if (self.process_module) |*module| module.deinit(self.allocator);
                self.process_module = null;
            }
        }

        if (self.config.enable_policy_engine) {
            self.policy_engine = try policy.PolicyEngine.init(self.allocator, .{});
            errdefer {
                if (self.policy_engine) |*engine| engine.deinit(self.allocator);
                self.policy_engine = null;
            }
        }

        self.state = .running;
        self.emit(.{ .app_started = {} });
    }

    pub fn stop(self: *App) AppError!void {
        if (self.state == .stopped) return;
        if (self.state == .starting) return error.NotStarted;

        self.state = .stopping;
        defer self.state = .stopped;

        if (self.policy_engine) |*engine| {
            engine.deinit(self.allocator);
            self.policy_engine = null;
        }

        if (self.process_module) |*module| {
            module.deinit(self.allocator);
            self.process_module = null;
        }

        if (self.memory_module) |*module| {
            module.deinit(self.allocator);
            self.memory_module = null;
        }

        if (self.power_module) |*module| {
            module.deinit(self.allocator);
            self.power_module = null;
        }

        if (self.store) |*store| {
            store.deinit(self.allocator);
            self.store = null;
        }

        self.last_power_snapshot = null;
        self.last_memory_snapshot = null;
        self.emit(.{ .app_stopped = {} });
    }

    pub fn tick(self: *App) AppError!void {
        if (self.state != .running) return error.NotStarted;

        if (self.power_module) |*module| {
            const snapshot = try module.poll();
            self.last_power_snapshot = snapshot;
            self.emit(.{ .power_changed = snapshot });
        }

        if (self.memory_module) |*module| {
            const snapshot = try module.poll();
            self.last_memory_snapshot = snapshot;
            self.emit(.{ .memory_pressure_changed = snapshot });
        }

        if (self.policy_engine) |*engine| {
            const request = policy.OptimizationRequest{
                .power = self.last_power_snapshot,
                .memory = self.last_memory_snapshot,
            };

            self.emit(.{ .optimization_requested = request });

            const decision = try engine.evaluate(request);
            self.emit(.{ .optimization_completed = decision });
        }
    }

    pub fn requestOptimization(self: *App, reason: policy.OptimizationReason) AppError!policy.OptimizationDecision {
        if (self.state != .running) return error.NotStarted;
        if (self.policy_engine == null) return error.NotStarted;

        const request = policy.OptimizationRequest{
            .reason = reason,
            .power = self.last_power_snapshot,
            .memory = self.last_memory_snapshot,
        };

        self.emit(.{ .optimization_requested = request });

        var engine = &self.policy_engine.?;
        const decision = try engine.evaluate(request);
        self.emit(.{ .optimization_completed = decision });
        return decision;
    }

    fn emit(self: *App, event: Event) void {
        if (self.event_sink) |sink| {
            sink.emit(event);
        }
    }
};

test "app initializes in stopped state" {
    const allocator = std.testing.allocator;
    var app = App.init(allocator, .{
        .settings_path = "settings.json",
        .snapshots_path = "snapshots.json",
    });
    defer app.deinit();

    try std.testing.expectEqual(RuntimeState.stopped, app.state);
}
