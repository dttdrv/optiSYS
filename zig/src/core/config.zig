const std = @import("std");

pub const ConfigError = error{
    InvalidValue,
    MissingField,
    UnsupportedVersion,
    OutOfMemory,
    ParseFailure,
    SerializeFailure,
};

pub const RuntimeMode = enum {
    normal,
    read_only,
    degraded,
};

pub const ThemeMode = enum {
    system,
    light,
    dark,
};

pub const OptimizationLevel = enum {
    conservative,
    balanced,
    aggressive,
};

pub const PowerSourceAction = enum {
    activate,
    deactivate,
    do_nothing,
};

pub const WindowPlacement = struct {
    width: f64 = 1000,
    height: f64 = 700,
    left: ?f64 = null,
    top: ?f64 = null,

    pub fn validate(self: *WindowPlacement) void {
        self.width = clampFloat(self.width, 400, 4000, 1000);
        self.height = clampFloat(self.height, 300, 3000, 700);

        if (self.left) |value| {
            if (!std.math.isFinite(value)) self.left = null;
        }
        if (self.top) |value| {
            if (!std.math.isFinite(value)) self.top = null;
        }
    }
};

pub const BatteryConfig = struct {
    auto_optimize_on_battery: bool = true,
    debounce_power_change_seconds: u32 = 2,
    poll_interval_on_ac_seconds: u32 = 15,
    poll_interval_on_battery_seconds: u32 = 5,
    savings_average_window_seconds: u32 = 60,
    action_on_ac: PowerSourceAction = .deactivate,
    action_on_battery: PowerSourceAction = .activate,

    pub fn validate(self: *BatteryConfig) void {
        self.debounce_power_change_seconds = clampInt(u32, self.debounce_power_change_seconds, 1, 10, 2);
        self.poll_interval_on_ac_seconds = clampInt(u32, self.poll_interval_on_ac_seconds, 5, 300, 15);
        self.poll_interval_on_battery_seconds = clampInt(u32, self.poll_interval_on_battery_seconds, 2, 120, 5);
        self.savings_average_window_seconds = clampInt(u32, self.savings_average_window_seconds, 10, 600, 60);
    }
};

pub const MemoryConfig = struct {
    auto_optimize_enabled: bool = false,
    check_interval_seconds: u32 = 5,
    threshold_percent: u8 = 80,
    cooldown_seconds: u32 = 30,
    hysteresis_gap: u8 = 10,
    trend_window_size: u32 = 10,
    predictive_lead_seconds: u32 = 15,
    accessed_bits_delay_ms: u32 = 2000,
    effectiveness_tracking_enabled: bool = true,
    scheduled_optimize_enabled: bool = false,
    scheduled_optimize_interval_minutes: u32 = 30,
    self_working_set_cap_mb: u32 = 25,
    cache_max_percent: u8 = 0,
    level: OptimizationLevel = .balanced,

    pub fn validate(self: *MemoryConfig) void {
        self.check_interval_seconds = clampInt(u32, self.check_interval_seconds, 1, 60, 5);
        self.threshold_percent = clampInt(u8, self.threshold_percent, 10, 95, 80);
        self.cooldown_seconds = clampInt(u32, self.cooldown_seconds, 5, 300, 30);
        self.hysteresis_gap = clampInt(u8, self.hysteresis_gap, 1, 30, 10);
        self.trend_window_size = clampInt(u32, self.trend_window_size, 3, 60, 10);
        self.predictive_lead_seconds = clampInt(u32, self.predictive_lead_seconds, 5, 120, 15);
        self.accessed_bits_delay_ms = clampInt(u32, self.accessed_bits_delay_ms, 0, 5000, 2000);
        self.scheduled_optimize_interval_minutes = clampInt(u32, self.scheduled_optimize_interval_minutes, 1, 1440, 30);
        self.self_working_set_cap_mb = clampInt(u32, self.self_working_set_cap_mb, 0, 256, 25);
        self.cache_max_percent = clampInt(u8, self.cache_max_percent, 0, 75, 0);
    }
};

pub const DomainConfig = struct {
    eco_qos_enabled: bool = true,
    timer_resolution_enabled: bool = true,
    background_services_enabled: bool = true,
    usb_suspend_enabled: bool = true,
    network_power_enabled: bool = true,
    gpu_power_enabled: bool = true,
    cpu_parking_enabled: bool = true,
    disk_coalescing_enabled: bool = true,

    pub fn validate(self: *DomainConfig) void {
        _ = self;
    }
};

pub const SafetyConfig = struct {
    preserve_user_settings: bool = true,
    protect_foreground_work: bool = true,
    allow_temporary_system_changes: bool = true,
    allow_background_service_control: bool = false,
    allow_device_registry_mutation: bool = false,
    runtime_mode: RuntimeMode = .normal,

    pub fn validate(self: *SafetyConfig) void {
        _ = self;
    }
};

pub const UiConfig = struct {
    minimize_to_tray: bool = true,
    start_with_windows: bool = false,
    theme_mode: ThemeMode = .system,
    window: WindowPlacement = .{},

    pub fn validate(self: *UiConfig) void {
        self.window.validate();
    }
};

pub const Config = struct {
    pub const current_version: u32 = 1;

    version: u32 = current_version,
    battery: BatteryConfig = .{},
    memory: MemoryConfig = .{},
    domains: DomainConfig = .{},
    safety: SafetyConfig = .{},
    ui: UiConfig = .{},
    excluded_processes: std.ArrayListUnmanaged([]u8) = .empty,
    eco_qos_excluded_processes: std.ArrayListUnmanaged([]u8) = .empty,
    services_to_throttle: std.ArrayListUnmanaged([]u8) = .empty,

    pub fn initDefaults(allocator: std.mem.Allocator) !Config {
        var config = Config{};
        errdefer config.deinit(allocator);

        try appendDefaultStrings(allocator, &config.excluded_processes, default_excluded_processes);
        try appendDefaultStrings(allocator, &config.eco_qos_excluded_processes, default_eco_qos_excluded_processes);
        try appendDefaultStrings(allocator, &config.services_to_throttle, default_services_to_throttle);

        config.validate();
        return config;
    }

    pub fn deinit(self: *Config, allocator: std.mem.Allocator) void {
        freeStringList(allocator, &self.excluded_processes);
        freeStringList(allocator, &self.eco_qos_excluded_processes);
        freeStringList(allocator, &self.services_to_throttle);
        self.* = undefined;
    }

    pub fn clone(self: *const Config, allocator: std.mem.Allocator) !Config {
        var copy = Config{
            .version = self.version,
            .battery = self.battery,
            .memory = self.memory,
            .domains = self.domains,
            .safety = self.safety,
            .ui = self.ui,
        };
        errdefer copy.deinit(allocator);

        try cloneStringList(allocator, self.excluded_processes.items, &copy.excluded_processes);
        try cloneStringList(allocator, self.eco_qos_excluded_processes.items, &copy.eco_qos_excluded_processes);
        try cloneStringList(allocator, self.services_to_throttle.items, &copy.services_to_throttle);

        return copy;
    }

    pub fn validate(self: *Config) void {
        if (self.version == 0 or self.version > current_version) {
            self.version = current_version;
        }

        self.battery.validate();
        self.memory.validate();
        self.domains.validate();
        self.safety.validate();
        self.ui.validate();
    }

    pub fn ensureDefaults(self: *Config, allocator: std.mem.Allocator) !void {
        if (self.excluded_processes.items.len == 0) {
            try appendDefaultStrings(allocator, &self.excluded_processes, default_excluded_processes);
        }
        if (self.eco_qos_excluded_processes.items.len == 0) {
            try appendDefaultStrings(allocator, &self.eco_qos_excluded_processes, default_eco_qos_excluded_processes);
        }
        if (self.services_to_throttle.items.len == 0) {
            try appendDefaultStrings(allocator, &self.services_to_throttle, default_services_to_throttle);
        }
    }

    pub fn toJsonAlloc(self: *const Config, allocator: std.mem.Allocator) ![]u8 {
        var json = std.ArrayList(u8).init(allocator);
        errdefer json.deinit();

        try self.writeJson(json.writer());
        return json.toOwnedSlice();
    }

    pub fn writeJson(self: *const Config, writer: anytype) !void {
        try writer.writeAll("{\n");
        try writer.print("  \"version\": {},\n", .{self.version});

        try writer.writeAll("  \"battery\": ");
        try std.json.stringify(self.battery, .{ .whitespace = .indent_2 }, writer);
        try writer.writeAll(",\n");

        try writer.writeAll("  \"memory\": ");
        try std.json.stringify(self.memory, .{ .whitespace = .indent_2 }, writer);
        try writer.writeAll(",\n");

        try writer.writeAll("  \"domains\": ");
        try std.json.stringify(self.domains, .{ .whitespace = .indent_2 }, writer);
        try writer.writeAll(",\n");

        try writer.writeAll("  \"safety\": ");
        try std.json.stringify(self.safety, .{ .whitespace = .indent_2 }, writer);
        try writer.writeAll(",\n");

        try writer.writeAll("  \"ui\": ");
        try std.json.stringify(self.ui, .{ .whitespace = .indent_2 }, writer);
        try writer.writeAll(",\n");

        try writer.writeAll("  \"excluded_processes\": ");
        try writeStringArray(writer, self.excluded_processes.items);
        try writer.writeAll(",\n");

        try writer.writeAll("  \"eco_qos_excluded_processes\": ");
        try writeStringArray(writer, self.eco_qos_excluded_processes.items);
        try writer.writeAll(",\n");

        try writer.writeAll("  \"services_to_throttle\": ");
        try writeStringArray(writer, self.services_to_throttle.items);
        try writer.writeAll("\n");

        try writer.writeAll("}\n");
    }
};

pub const ParsedConfig = struct {
    version: ?u32 = null,
    battery: ?BatteryConfig = null,
    memory: ?MemoryConfig = null,
    domains: ?DomainConfig = null,
    safety: ?SafetyConfig = null,
    ui: ?UiConfig = null,
    excluded_processes: ?[][]const u8 = null,
    eco_qos_excluded_processes: ?[][]const u8 = null,
    services_to_throttle: ?[][]const u8 = null,
};

pub fn parseJson(allocator: std.mem.Allocator, bytes: []const u8) !Config {
    var parsed = try std.json.parseFromSlice(ParsedConfig, allocator, bytes, .{
        .ignore_unknown_fields = true,
        .allocate = .alloc_always,
    });
    defer parsed.deinit();

    var config = try Config.initDefaults(allocator);
    errdefer config.deinit(allocator);

    if (parsed.value.version) |value| config.version = value;
    if (parsed.value.battery) |value| config.battery = value;
    if (parsed.value.memory) |value| config.memory = value;
    if (parsed.value.domains) |value| config.domains = value;
    if (parsed.value.safety) |value| config.safety = value;
    if (parsed.value.ui) |value| config.ui = value;

    if (parsed.value.excluded_processes) |items| {
        replaceStringList(allocator, &config.excluded_processes, items) catch return error.OutOfMemory;
    }
    if (parsed.value.eco_qos_excluded_processes) |items| {
        replaceStringList(allocator, &config.eco_qos_excluded_processes, items) catch return error.OutOfMemory;
    }
    if (parsed.value.services_to_throttle) |items| {
        replaceStringList(allocator, &config.services_to_throttle, items) catch return error.OutOfMemory;
    }

    config.validate();
    try config.ensureDefaults(allocator);
    return config;
}

fn writeStringArray(writer: anytype, items: []const []u8) !void {
    try writer.writeAll("[");
    for (items, 0..) |item, index| {
        if (index != 0) try writer.writeAll(", ");
        try std.json.stringify(item, .{}, writer);
    }
    try writer.writeAll("]");
}

fn appendDefaultStrings(
    allocator: std.mem.Allocator,
    list: *std.ArrayListUnmanaged([]u8),
    defaults: []const []const u8,
) !void {
    for (defaults) |item| {
        try list.append(allocator, try allocator.dupe(u8, item));
    }
}

fn cloneStringList(
    allocator: std.mem.Allocator,
    source: []const []u8,
    destination: *std.ArrayListUnmanaged([]u8),
) !void {
    for (source) |item| {
        try destination.append(allocator, try allocator.dupe(u8, item));
    }
}

fn replaceStringList(
    allocator: std.mem.Allocator,
    list: *std.ArrayListUnmanaged([]u8),
    items: []const []const u8,
) !void {
    freeStringList(allocator, list);
    for (items) |item| {
        try list.append(allocator, try allocator.dupe(u8, item));
    }
}

fn freeStringList(allocator: std.mem.Allocator, list: *std.ArrayListUnmanaged([]u8)) void {
    for (list.items) |item| {
        allocator.free(item);
    }
    list.deinit(allocator);
    list.* = .empty;
}

fn clampFloat(value: f64, min: f64, max: f64, fallback: f64) f64 {
    if (!std.math.isFinite(value)) return fallback;
    return std.math.clamp(value, min, max);
}

fn clampInt(comptime T: type, value: T, min: T, max: T, fallback: T) T {
    if (min > max) return fallback;
    return std.math.clamp(value, min, max);
}

const default_excluded_processes = [_][]const u8{
    "System",
    "Idle",
    "smss",
    "csrss",
    "wininit",
    "services",
    "lsass",
    "svchost",
    "dwm",
    "winlogon",
    "Memory Compression",
    "Registry",
    "fontdrvhost",
    "conhost",
};

const default_eco_qos_excluded_processes = [_][]const u8{
    "System",
    "Idle",
    "smss",
    "csrss",
    "wininit",
    "services",
    "lsass",
    "svchost",
    "dwm",
    "winlogon",
    "fontdrvhost",
    "conhost",
    "Memory Compression",
    "Registry",
};

const default_services_to_throttle = [_][]const u8{
    "WSearch",
    "SysMain",
    "DiagTrack",
    "BITS",
    "wuauserv",
    "DoSvc",
    "DPS",
    "WdiServiceHost",
};

test "default config validates and contains defaults" {
    var config = try Config.initDefaults(std.testing.allocator);
    defer config.deinit(std.testing.allocator);

    try std.testing.expectEqual(@as(u32, Config.current_version), config.version);
    try std.testing.expect(config.excluded_processes.items.len > 0);
    try std.testing.expect(config.eco_qos_excluded_processes.items.len > 0);
    try std.testing.expect(config.services_to_throttle.items.len > 0);
}

test "config validation clamps invalid values" {
    var config = try Config.initDefaults(std.testing.allocator);
    defer config.deinit(std.testing.allocator);

    config.memory.threshold_percent = 100;
    config.memory.cooldown_seconds = 1;
    config.battery.debounce_power_change_seconds = 0;
    config.ui.window.width = std.math.inf(f64);

    config.validate();

    try std.testing.expectEqual(@as(u8, 95), config.memory.threshold_percent);
    try std.testing.expectEqual(@as(u32, 5), config.memory.cooldown_seconds);
    try std.testing.expectEqual(@as(u32, 1), config.battery.debounce_power_change_seconds);
    try std.testing.expectEqual(@as(f64, 1000), config.ui.window.width);
}

test "config round trips through json" {
    var config = try Config.initDefaults(std.testing.allocator);
    defer config.deinit(std.testing.allocator);

    config.memory.auto_optimize_enabled = true;
    config.battery.auto_optimize_on_battery = false;

    const json = try config.toJsonAlloc(std.testing.allocator);
    defer std.testing.allocator.free(json);

    var parsed = try parseJson(std.testing.allocator, json);
    defer parsed.deinit(std.testing.allocator);

    try std.testing.expectEqual(config.memory.auto_optimize_enabled, parsed.memory.auto_optimize_enabled);
    try std.testing.expectEqual(config.battery.auto_optimize_on_battery, parsed.battery.auto_optimize_on_battery);
    try std.testing.expect(parsed.excluded_processes.items.len > 0);
}
