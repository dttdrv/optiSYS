const std = @import("std");
const types = @import("types.zig");

pub const TelemetryError = error{
    OutOfMemory,
    InvalidCapacity,
};

pub const Event = types.Event;
pub const Severity = types.Severity;
pub const ModuleId = types.ModuleId;
pub const DomainId = types.DomainId;
pub const EffectivenessSample = types.EffectivenessSample;
pub const TriggerReason = types.TriggerReason;
pub const TimestampMs = types.TimestampMs;

pub const EventFilter = struct {
    minimum_severity: Severity = .debug,
    module_id: ?ModuleId = null,
    domain_id: ?DomainId = null,

    pub fn matches(self: EventFilter, event: Event) bool {
        if (@intFromEnum(event.severity) < @intFromEnum(self.minimum_severity)) return false;
        if (self.module_id) |module_id| {
            if (event.module_id != module_id) return false;
        }
        if (self.domain_id) |domain_id| {
            if (event.domain_id != domain_id) return false;
        }
        return true;
    }
};

pub const EventStats = struct {
    total: usize = 0,
    debug: usize = 0,
    info: usize = 0,
    warn: usize = 0,
    err: usize = 0,

    pub fn record(self: *EventStats, severity: Severity) void {
        self.total += 1;
        switch (severity) {
            .debug => self.debug += 1,
            .info => self.info += 1,
            .warn => self.warn += 1,
            .err => self.err += 1,
        }
    }
};

pub const EffectivenessSummary = struct {
    sample_count: usize = 0,
    total_freed_bytes: u64 = 0,
    average_freed_bytes: u64 = 0,
    average_duration_ms: u32 = 0,
    average_confidence: f32 = 0.0,
    best_freed_bytes: u64 = 0,

    pub fn empty() EffectivenessSummary {
        return .{};
    }
};

pub const EventRing = struct {
    allocator: std.mem.Allocator,
    entries: []Event,
    next_index: usize = 0,
    count: usize = 0,
    stats: EventStats = .{},

    pub fn init(allocator: std.mem.Allocator, capacity: usize) TelemetryError!EventRing {
        if (capacity == 0) return error.InvalidCapacity;

        const entries = allocator.alloc(Event, capacity) catch return error.OutOfMemory;
        return .{
            .allocator = allocator,
            .entries = entries,
        };
    }

    pub fn deinit(self: *EventRing) void {
        self.allocator.free(self.entries);
        self.* = undefined;
    }

    pub fn capacity(self: EventRing) usize {
        return self.entries.len;
    }

    pub fn len(self: EventRing) usize {
        return self.count;
    }

    pub fn isEmpty(self: EventRing) bool {
        return self.count == 0;
    }

    pub fn push(self: *EventRing, event: Event) void {
        self.entries[self.next_index] = event;
        self.next_index = (self.next_index + 1) % self.entries.len;
        if (self.count < self.entries.len) {
            self.count += 1;
        }
        self.stats.record(event.severity);
    }

    pub fn newest(self: EventRing) ?Event {
        if (self.count == 0) return null;
        const index = if (self.next_index == 0) self.entries.len - 1 else self.next_index - 1;
        return self.entries[index];
    }

    pub fn oldest(self: EventRing) ?Event {
        if (self.count == 0) return null;
        return self.entries[self.oldestIndex()];
    }

    pub fn snapshot(self: EventRing, allocator: std.mem.Allocator) TelemetryError![]Event {
        const out = allocator.alloc(Event, self.count) catch return error.OutOfMemory;
        errdefer allocator.free(out);

        var i: usize = 0;
        while (i < self.count) : (i += 1) {
            out[i] = self.at(i).?;
        }

        return out;
    }

    pub fn filteredSnapshot(
        self: EventRing,
        allocator: std.mem.Allocator,
        filter: EventFilter,
    ) TelemetryError![]Event {
        var matched: usize = 0;
        var i: usize = 0;
        while (i < self.count) : (i += 1) {
            const event = self.at(i).?;
            if (filter.matches(event)) matched += 1;
        }

        const out = allocator.alloc(Event, matched) catch return error.OutOfMemory;
        errdefer allocator.free(out);

        var out_index: usize = 0;
        i = 0;
        while (i < self.count) : (i += 1) {
            const event = self.at(i).?;
            if (!filter.matches(event)) continue;
            out[out_index] = event;
            out_index += 1;
        }

        return out;
    }

    pub fn at(self: EventRing, logical_index: usize) ?Event {
        if (logical_index >= self.count) return null;
        const physical_index = (self.oldestIndex() + logical_index) % self.entries.len;
        return self.entries[physical_index];
    }

    fn oldestIndex(self: EventRing) usize {
        if (self.count < self.entries.len) return 0;
        return self.next_index;
    }
};

pub const EffectivenessTracker = struct {
    allocator: std.mem.Allocator,
    samples: std.ArrayList(EffectivenessSample),

    pub fn init(allocator: std.mem.Allocator) EffectivenessTracker {
        return .{
            .allocator = allocator,
            .samples = std.ArrayList(EffectivenessSample).empty,
        };
    }

    pub fn deinit(self: *EffectivenessTracker) void {
        self.samples.deinit(self.allocator);
        self.* = undefined;
    }

    pub fn record(self: *EffectivenessTracker, sample: EffectivenessSample) TelemetryError!void {
        self.samples.append(self.allocator, sample) catch return error.OutOfMemory;
    }

    pub fn len(self: EffectivenessTracker) usize {
        return self.samples.items.len;
    }

    pub fn isEmpty(self: EffectivenessTracker) bool {
        return self.samples.items.len == 0;
    }

    pub fn clear(self: *EffectivenessTracker) void {
        self.samples.clearRetainingCapacity();
    }

    pub fn summarizeAll(self: EffectivenessTracker) EffectivenessSummary {
        return summarizeSlice(self.samples.items);
    }

    pub fn summarizeDomain(self: EffectivenessTracker, domain_id: DomainId) EffectivenessSummary {
        var count: usize = 0;
        var total_freed: u64 = 0;
        var total_duration: u64 = 0;
        var total_confidence: f64 = 0.0;
        var best_freed: u64 = 0;

        for (self.samples.items) |sample| {
            if (sample.domain_id != domain_id) continue;

            const freed = sample.freedBytes();
            count += 1;
            total_freed += freed;
            total_duration += sample.duration_ms;
            total_confidence += sample.confidence;
            if (freed > best_freed) best_freed = freed;
        }

        if (count == 0) return .{};

        return .{
            .sample_count = count,
            .total_freed_bytes = total_freed,
            .average_freed_bytes = total_freed / count,
            .average_duration_ms = @intCast(total_duration / count),
            .average_confidence = @floatCast(total_confidence / @as(f64, @floatFromInt(count))),
            .best_freed_bytes = best_freed,
        };
    }

    pub fn summarizeTrigger(self: EffectivenessTracker, trigger_reason: TriggerReason) EffectivenessSummary {
        var count: usize = 0;
        var total_freed: u64 = 0;
        var total_duration: u64 = 0;
        var total_confidence: f64 = 0.0;
        var best_freed: u64 = 0;

        for (self.samples.items) |sample| {
            if (sample.trigger_reason != trigger_reason) continue;

            const freed = sample.freedBytes();
            count += 1;
            total_freed += freed;
            total_duration += sample.duration_ms;
            total_confidence += sample.confidence;
            if (freed > best_freed) best_freed = freed;
        }

        if (count == 0) return .{};

        return .{
            .sample_count = count,
            .total_freed_bytes = total_freed,
            .average_freed_bytes = total_freed / count,
            .average_duration_ms = @intCast(total_duration / count),
            .average_confidence = @floatCast(total_confidence / @as(f64, @floatFromInt(count))),
            .best_freed_bytes = best_freed,
        };
    }

    pub fn latestForDomain(self: EffectivenessTracker, domain_id: DomainId) ?EffectivenessSample {
        var i = self.samples.items.len;
        while (i > 0) {
            i -= 1;
            const sample = self.samples.items[i];
            if (sample.domain_id == domain_id) return sample;
        }
        return null;
    }

    pub fn snapshot(self: EffectivenessTracker, allocator: std.mem.Allocator) TelemetryError![]EffectivenessSample {
        return allocator.dupe(EffectivenessSample, self.samples.items) catch error.OutOfMemory;
    }

    fn summarizeSlice(samples: []const EffectivenessSample) EffectivenessSummary {
        if (samples.len == 0) return .{};

        var total_freed: u64 = 0;
        var total_duration: u64 = 0;
        var total_confidence: f64 = 0.0;
        var best_freed: u64 = 0;

        for (samples) |sample| {
            const freed = sample.freedBytes();
            total_freed += freed;
            total_duration += sample.duration_ms;
            total_confidence += sample.confidence;
            if (freed > best_freed) best_freed = freed;
        }

        return .{
            .sample_count = samples.len,
            .total_freed_bytes = total_freed,
            .average_freed_bytes = total_freed / samples.len,
            .average_duration_ms = @intCast(total_duration / samples.len),
            .average_confidence = @floatCast(total_confidence / @as(f64, @floatFromInt(samples.len))),
            .best_freed_bytes = best_freed,
        };
    }
};

pub fn makeEvent(
    timestamp_ms: TimestampMs,
    severity: Severity,
    module_id: ModuleId,
    domain_id: ?DomainId,
    message: []const u8,
) Event {
    return .{
        .timestamp_ms = timestamp_ms,
        .severity = severity,
        .module_id = module_id,
        .domain_id = domain_id,
        .message = message,
    };
}

test "event filter matches severity and optional ids" {
    const event = makeEvent(1, .warn, .memory, .memory_optimizer, "warn");

    try std.testing.expect(EventFilter{ .minimum_severity = .debug }.matches(event));
    try std.testing.expect(EventFilter{ .minimum_severity = .warn }.matches(event));
    try std.testing.expect(!EventFilter{ .minimum_severity = .err }.matches(event));
    try std.testing.expect(EventFilter{ .module_id = .memory }.matches(event));
    try std.testing.expect(!EventFilter{ .module_id = .power }.matches(event));
    try std.testing.expect(EventFilter{ .domain_id = .memory_optimizer }.matches(event));
    try std.testing.expect(!EventFilter{ .domain_id = .eco_qos }.matches(event));
}

test "event ring preserves chronological order across wraparound" {
    var ring = try EventRing.init(std.testing.allocator, 3);
    defer ring.deinit();

    ring.push(makeEvent(1, .info, .system, null, "one"));
    ring.push(makeEvent(2, .info, .system, null, "two"));
    ring.push(makeEvent(3, .info, .system, null, "three"));
    ring.push(makeEvent(4, .warn, .system, null, "four"));

    try std.testing.expectEqual(@as(usize, 3), ring.len());
    try std.testing.expectEqualStrings("two", ring.at(0).?.message);
    try std.testing.expectEqualStrings("three", ring.at(1).?.message);
    try std.testing.expectEqualStrings("four", ring.at(2).?.message);
    try std.testing.expectEqualStrings("two", ring.oldest().?.message);
    try std.testing.expectEqualStrings("four", ring.newest().?.message);
}

test "event ring filtered snapshot returns only matching events" {
    var ring = try EventRing.init(std.testing.allocator, 5);
    defer ring.deinit();

    ring.push(makeEvent(1, .debug, .power, .battery_monitor, "a"));
    ring.push(makeEvent(2, .warn, .memory, .memory_monitor, "b"));
    ring.push(makeEvent(3, .err, .memory, .memory_optimizer, "c"));

    const filtered = try ring.filteredSnapshot(std.testing.allocator, .{
        .minimum_severity = .warn,
        .module_id = .memory,
    });
    defer std.testing.allocator.free(filtered);

    try std.testing.expectEqual(@as(usize, 2), filtered.len);
    try std.testing.expectEqualStrings("b", filtered[0].message);
    try std.testing.expectEqualStrings("c", filtered[1].message);
}

test "effectiveness tracker summarizes domain samples" {
    var tracker = EffectivenessTracker.init(std.testing.allocator);
    defer tracker.deinit();

    try tracker.record(.{
        .timestamp_ms = 1,
        .domain_id = .memory_optimizer,
        .trigger_reason = .manual,
        .duration_ms = 10,
        .before_available_bytes = 100,
        .after_available_bytes = 150,
        .confidence = 0.5,
    });
    try tracker.record(.{
        .timestamp_ms = 2,
        .domain_id = .memory_optimizer,
        .trigger_reason = .manual,
        .duration_ms = 30,
        .before_available_bytes = 150,
        .after_available_bytes = 250,
        .confidence = 1.0,
    });
    try tracker.record(.{
        .timestamp_ms = 3,
        .domain_id = .eco_qos,
        .trigger_reason = .power_source_changed,
        .duration_ms = 5,
        .before_available_bytes = 0,
        .after_available_bytes = 0,
        .confidence = 0.25,
    });

    const summary = tracker.summarizeDomain(.memory_optimizer);
    try std.testing.expectEqual(@as(usize, 2), summary.sample_count);
    try std.testing.expectEqual(@as(u64, 150), summary.total_freed_bytes);
    try std.testing.expectEqual(@as(u64, 75), summary.average_freed_bytes);
    try std.testing.expectEqual(@as(u32, 20), summary.average_duration_ms);
    try std.testing.expectApproxEqAbs(@as(f32, 0.75), summary.average_confidence, 0.0001);
    try std.testing.expectEqual(@as(u64, 100), summary.best_freed_bytes);
}

test "effectiveness tracker latestForDomain returns newest matching sample" {
    var tracker = EffectivenessTracker.init(std.testing.allocator);
    defer tracker.deinit();

    try tracker.record(.{
        .timestamp_ms = 10,
        .domain_id = .eco_qos,
        .trigger_reason = .manual,
        .duration_ms = 1,
        .before_available_bytes = 0,
        .after_available_bytes = 0,
        .confidence = 0.1,
    });
    try tracker.record(.{
        .timestamp_ms = 20,
        .domain_id = .memory_optimizer,
        .trigger_reason = .memory_threshold,
        .duration_ms = 2,
        .before_available_bytes = 100,
        .after_available_bytes = 200,
        .confidence = 0.9,
    });
    try tracker.record(.{
        .timestamp_ms = 30,
        .domain_id = .memory_optimizer,
        .trigger_reason = .predictive_memory_threshold,
        .duration_ms = 3,
        .before_available_bytes = 200,
        .after_available_bytes = 260,
        .confidence = 0.8,
    });

    const latest = tracker.latestForDomain(.memory_optimizer).?;
    try std.testing.expectEqual(@as(TimestampMs, 30), latest.timestamp_ms);
    try std.testing.expectEqual(TriggerReason.predictive_memory_threshold, latest.trigger_reason);
}
