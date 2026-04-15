//! Shared core types and contracts for the non-UI `optiSYS` Zig backend.
//!
//! Design goals:
//! - Keep these types host-agnostic and reusable across battery, memory, and future modules.
//! - Prefer explicit state and error handling over hidden behavior.
//! - Model optimization as transactional, reversible, and capability-aware.
//! - Avoid embedding UI concerns in the core.

const std = @import("std");

pub const TimestampMs = i64;
pub const ModuleId = enum {
    power,
    memory,
    process,
    policy,
    persistence,
    system,
};

pub const DomainId = enum {
    battery_monitor,
    power_source_monitor,
    eco_qos,
    timer_resolution,
    background_services,
    usb_suspend,
    network_power,
    gpu_power,
    cpu_parking,
    disk_coalescing,
    memory_monitor,
    memory_optimizer,
    process_snapshot,
};

pub const OptimizationKind = enum {
    advisory,
    scoped_runtime,
    temporary_system,
};

pub const OptimizationPhase = enum {
    idle,
    probing,
    capture_baseline,
    applying,
    verifying,
    committed,
    reverting,
    rolled_back,
    failed,
};

pub const TriggerReason = enum {
    manual,
    startup_recovery,
    power_source_changed,
    battery_discharge_trend,
    low_memory_notification,
    memory_threshold,
    predictive_memory_threshold,
    scheduled,
    periodic_refresh,
};

pub const Severity = enum {
    debug,
    info,
    warn,
    err,
};

pub const PowerSource = enum {
    unknown,
    ac,
    battery,
};

pub const BatteryChargeState = enum {
    unknown,
    charging,
    discharging,
    idle,
};

pub const OptimizationLevel = enum {
    conservative,
    balanced,
    aggressive,
};

pub const SupportLevel = enum {
    unsupported,
    partial,
    supported,
};

pub const CapabilityFlag = packed struct(u32) {
    can_monitor_power_source: bool = false,
    can_read_battery_state: bool = false,
    can_set_process_power_throttling: bool = false,
    can_set_process_memory_priority: bool = false,
    can_trim_working_sets: bool = false,
    can_receive_low_memory_notifications: bool = false,
    can_control_services: bool = false,
    can_modify_power_scheme: bool = false,
    can_persist_state: bool = false,
    reserved: u23 = 0,
};

pub const ResourceBudget = struct {
    max_cpu_percent: f32 = 1.0,
    max_private_bytes: u64 = 64 * 1024 * 1024,
    max_wakeups_per_second: u32 = 2,
    max_disk_writes_per_minute: u32 = 12,
};

pub const RuntimeMode = enum {
    normal,
    read_only,
    degraded,
};

pub const AppPolicy = struct {
    runtime_mode: RuntimeMode = .normal,
    optimization_level: OptimizationLevel = .balanced,
    preserve_user_settings: bool = true,
    protect_foreground_work: bool = true,
    allow_temporary_system_changes: bool = true,
    allow_background_service_control: bool = false,
    allow_device_registry_mutation: bool = false,
    enable_predictive_optimization: bool = true,
    enable_effectiveness_tracking: bool = true,
    resource_budget: ResourceBudget = .{},
};

pub const BatteryInfo = struct {
    has_battery: bool = false,
    power_source: PowerSource = .unknown,
    charge_state: BatteryChargeState = .unknown,
    charge_percent: u8 = 0,
    drain_rate_mw: i32 = 0,
    estimated_time_remaining_s: ?u32 = null,

    pub fn watts(self: BatteryInfo) f64 {
        const abs_rate: i32 = if (self.drain_rate_mw < 0) -self.drain_rate_mw else self.drain_rate_mw;
        return @as(f64, @floatFromInt(abs_rate)) / 1000.0;
    }
};

pub const MemoryInfo = struct {
    total_physical_bytes: u64 = 0,
    available_physical_bytes: u64 = 0,
    cached_bytes: u64 = 0,
    standby_bytes: u64 = 0,
    free_bytes: u64 = 0,
    modified_bytes: u64 = 0,
    compressed_bytes: u64 = 0,
    commit_total_bytes: u64 = 0,
    commit_limit_bytes: u64 = 0,
    process_count: u32 = 0,
    thread_count: u32 = 0,
    handle_count: u32 = 0,
    low_memory: bool = false,

    pub fn usagePercent(self: MemoryInfo) f32 {
        if (self.total_physical_bytes == 0) return 0;
        const used = self.total_physical_bytes -| self.available_physical_bytes;
        return (@as(f32, @floatFromInt(used)) / @as(f32, @floatFromInt(self.total_physical_bytes))) * 100.0;
    }
};

pub const ProcessSnapshot = struct {
    pid: u32,
    name: []const u8,
    working_set_bytes: u64,
    private_bytes: u64 = 0,
    is_foreground: bool = false,
    is_excluded: bool = false,
    is_system_critical: bool = false,
};

pub const DomainStatus = struct {
    domain_id: DomainId,
    support_level: SupportLevel = .unsupported,
    is_enabled: bool = false,
    is_active: bool = false,
    summary: []const u8 = "",
};

pub const Event = struct {
    timestamp_ms: TimestampMs,
    severity: Severity,
    module_id: ModuleId,
    domain_id: ?DomainId = null,
    message: []const u8,
};

pub const EffectivenessSample = struct {
    timestamp_ms: TimestampMs,
    domain_id: DomainId,
    trigger_reason: TriggerReason,
    duration_ms: u32,
    before_available_bytes: u64,
    after_available_bytes: u64,
    confidence: f32 = 0.0,

    pub fn freedBytes(self: EffectivenessSample) u64 {
        return self.after_available_bytes -| self.before_available_bytes;
    }
};

pub const BaselineBlob = struct {
    allocator: std.mem.Allocator,
    bytes: []u8,

    pub fn initCopy(allocator: std.mem.Allocator, source: []const u8) !BaselineBlob {
        return .{
            .allocator = allocator,
            .bytes = try allocator.dupe(u8, source),
        };
    }

    pub fn deinit(self: *BaselineBlob) void {
        self.allocator.free(self.bytes);
        self.* = undefined;
    }
};

pub const ApplyStats = struct {
    optimized: u32 = 0,
    failed: u32 = 0,
    skipped: u32 = 0,
};

pub const ApplyResult = struct {
    domain_id: DomainId,
    success: bool,
    message: []const u8,
    stats: ApplyStats = .{},
    duration_ms: u32 = 0,
};

pub const TransactionId = u64;

pub const TransactionSummary = struct {
    id: TransactionId,
    trigger_reason: TriggerReason,
    phase: OptimizationPhase = .idle,
    started_at_ms: TimestampMs,
    finished_at_ms: ?TimestampMs = null,
    success_count: u32 = 0,
    failure_count: u32 = 0,
};

pub const DomainPlan = struct {
    domain_id: DomainId,
    kind: OptimizationKind = .advisory,
    enabled: bool = true,
    requires_admin: bool = false,
    reversible: bool = true,
};

pub const DomainContext = struct {
    allocator: std.mem.Allocator,
    now_ms: TimestampMs,
    policy: *const AppPolicy,
    trigger_reason: TriggerReason,
};

pub const ProbeResult = struct {
    support_level: SupportLevel,
    message: []const u8 = "",
};

pub const VerifyResult = struct {
    success: bool,
    message: []const u8 = "",
};

pub const DomainVTable = struct {
    probe: *const fn (ctx: *const DomainContext) anyerror!ProbeResult,
    capture_baseline: *const fn (ctx: *const DomainContext) anyerror!?BaselineBlob,
    apply: *const fn (ctx: *const DomainContext, baseline: ?*const BaselineBlob) anyerror!ApplyResult,
    verify: *const fn (ctx: *const DomainContext) anyerror!VerifyResult,
    revert: *const fn (ctx: *const DomainContext, baseline: ?*const BaselineBlob) anyerror!void,
};

pub const DomainContract = struct {
    plan: DomainPlan,
    vtable: DomainVTable,
};

pub const MonitorSample = union(enum) {
    battery: BatteryInfo,
    memory: MemoryInfo,
    power_source: PowerSource,
};

pub const MonitorCallback = *const fn (sample: MonitorSample) void;
pub const EventCallback = *const fn (event: Event) void;

pub const MonitorContract = struct {
    start: *const fn () anyerror!void,
    stop: *const fn () void,
};

pub const PersistenceContract = struct {
    save_bytes: *const fn (key: []const u8, bytes: []const u8) anyerror!void,
    load_bytes: *const fn (allocator: std.mem.Allocator, key: []const u8) anyerror!?[]u8,
    delete_key: *const fn (key: []const u8) anyerror!void,
};

pub const ClockContract = struct {
    now_ms: *const fn () TimestampMs,
};

pub const Error = error{
    AccessDenied,
    Unsupported,
    InvalidState,
    InvalidArgument,
    Timeout,
    OutOfMemory,
    IoFailure,
    SerializationFailure,
    DeserializationFailure,
    ProbeFailed,
    BaselineCaptureFailed,
    ApplyFailed,
    VerifyFailed,
    RevertFailed,
    PersistenceFailed,
};

pub fn domainIdString(domain_id: DomainId) []const u8 {
    return switch (domain_id) {
        .battery_monitor => "battery_monitor",
        .power_source_monitor => "power_source_monitor",
        .eco_qos => "eco_qos",
        .timer_resolution => "timer_resolution",
        .background_services => "background_services",
        .usb_suspend => "usb_suspend",
        .network_power => "network_power",
        .gpu_power => "gpu_power",
        .cpu_parking => "cpu_parking",
        .disk_coalescing => "disk_coalescing",
        .memory_monitor => "memory_monitor",
        .memory_optimizer => "memory_optimizer",
        .process_snapshot => "process_snapshot",
    };
}

pub fn moduleIdString(module_id: ModuleId) []const u8 {
    return switch (module_id) {
        .power => "power",
        .memory => "memory",
        .process => "process",
        .policy => "policy",
        .persistence => "persistence",
        .system => "system",
    };
}

test "battery watts conversion uses absolute drain rate" {
    const info = BatteryInfo{
        .drain_rate_mw = -12500,
    };
    try std.testing.expectApproxEqAbs(@as(f64, 12.5), info.watts(), 0.0001);
}

test "memory usage percent handles zero total safely" {
    const empty = MemoryInfo{};
    try std.testing.expectEqual(@as(f32, 0), empty.usagePercent());
}

test "memory usage percent computes expected value" {
    const info = MemoryInfo{
        .total_physical_bytes = 100,
        .available_physical_bytes = 25,
    };
    try std.testing.expectApproxEqAbs(@as(f32, 75.0), info.usagePercent(), 0.001);
}

test "effectiveness sample freed bytes saturates at zero" {
    const sample = EffectivenessSample{
        .timestamp_ms = 0,
        .domain_id = .memory_optimizer,
        .trigger_reason = .manual,
        .duration_ms = 1,
        .before_available_bytes = 200,
        .after_available_bytes = 150,
    };
    try std.testing.expectEqual(@as(u64, 0), sample.freedBytes());
}
