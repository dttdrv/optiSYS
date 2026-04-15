const std = @import("std");
const types = @import("types.zig");

pub const EngineError = types.Error || error{
    AlreadyRunning,
    NotRunning,
    EmptyPlan,
    DuplicateDomain,
};

pub const EngineConfig = struct {
    max_domains_per_transaction: usize = 32,
    stop_on_first_failure: bool = true,
    verify_after_apply: bool = true,
    rollback_on_failure: bool = true,
};

pub const TransactionOutcome = enum {
    success,
    partial_success,
    rolled_back,
    failed,
};

pub const DomainExecution = struct {
    contract: types.DomainContract,
    baseline: ?types.BaselineBlob = null,
    probe_result: ?types.ProbeResult = null,
    apply_result: ?types.ApplyResult = null,
    verify_result: ?types.VerifyResult = null,
    attempted_apply: bool = false,
    reverted: bool = false,

    pub fn deinit(self: *DomainExecution) void {
        if (self.baseline) |*baseline| {
            baseline.deinit();
            self.baseline = null;
        }
    }
};

pub const TransactionReport = struct {
    summary: types.TransactionSummary,
    outcome: TransactionOutcome,
    apply_results: []types.ApplyResult,
    reverted_domains: u32 = 0,

    pub fn deinit(self: *TransactionReport, allocator: std.mem.Allocator) void {
        allocator.free(self.apply_results);
        self.* = undefined;
    }
};

pub const Engine = struct {
    allocator: std.mem.Allocator,
    config: EngineConfig,
    next_transaction_id: types.TransactionId = 1,
    active_transaction: ?types.TransactionId = null,

    pub fn init(allocator: std.mem.Allocator, config: EngineConfig) Engine {
        return .{
            .allocator = allocator,
            .config = config,
        };
    }

    pub fn deinit(self: *Engine) void {
        _ = self;
    }

    pub fn isRunning(self: *const Engine) bool {
        return self.active_transaction != null;
    }

    pub fn execute(
        self: *Engine,
        ctx: *const types.DomainContext,
        plans: []const types.DomainContract,
    ) EngineError!TransactionReport {
        if (self.active_transaction != null) return error.AlreadyRunning;
        if (plans.len == 0) return error.EmptyPlan;
        if (plans.len > self.config.max_domains_per_transaction) return error.InvalidArgument;

        try validatePlans(plans);

        const transaction_id = self.next_transaction_id;
        self.next_transaction_id += 1;
        self.active_transaction = transaction_id;
        defer self.active_transaction = null;

        var summary = types.TransactionSummary{
            .id = transaction_id,
            .trigger_reason = ctx.trigger_reason,
            .phase = .probing,
            .started_at_ms = ctx.now_ms,
        };

        var executions = try self.allocator.alloc(DomainExecution, plans.len);
        defer {
            for (executions) |*execution| execution.deinit();
            self.allocator.free(executions);
        }

        for (plans, 0..) |plan, i| {
            executions[i] = .{
                .contract = plan,
            };
        }

        var apply_results = std.ArrayList(types.ApplyResult).init(self.allocator);
        defer apply_results.deinit();

        var applied_count: usize = 0;
        var failure_seen = false;

        summary.phase = .probing;
        try self.probeAll(ctx, executions);

        summary.phase = .capture_baseline;
        try self.captureBaselines(ctx, executions);

        summary.phase = .applying;
        for (executions) |*execution| {
            if (!execution.contract.plan.enabled) continue;
            if (execution.probe_result == null) continue;

            const probe = execution.probe_result.?;
            if (probe.support_level == .unsupported) {
                try apply_results.append(.{
                    .domain_id = execution.contract.plan.domain_id,
                    .success = false,
                    .message = "domain unsupported",
                    .stats = .{ .skipped = 1 },
                    .duration_ms = 0,
                });
                summary.failure_count += 1;
                failure_seen = true;
                if (self.config.stop_on_first_failure) break;
                continue;
            }

            const result = execution.contract.vtable.apply(
                ctx,
                if (execution.baseline) |*baseline| baseline else null,
            ) catch |err| {
                const failure = types.ApplyResult{
                    .domain_id = execution.contract.plan.domain_id,
                    .success = false,
                    .message = @errorName(err),
                    .stats = .{ .failed = 1 },
                    .duration_ms = 0,
                };
                execution.apply_result = failure;
                execution.attempted_apply = true;
                try apply_results.append(failure);
                summary.failure_count += 1;
                failure_seen = true;
                if (self.config.stop_on_first_failure) break;
                continue;
            };

            execution.apply_result = result;
            execution.attempted_apply = true;
            try apply_results.append(result);

            if (result.success) {
                summary.success_count += 1;
                applied_count += 1;
            } else {
                summary.failure_count += 1;
                failure_seen = true;
                if (self.config.stop_on_first_failure) break;
            }

            if (self.config.verify_after_apply and result.success) {
                summary.phase = .verifying;
                const verify = execution.contract.vtable.verify(ctx) catch |err| {
                    const failure = types.ApplyResult{
                        .domain_id = execution.contract.plan.domain_id,
                        .success = false,
                        .message = @errorName(err),
                        .stats = .{ .failed = 1 },
                        .duration_ms = 0,
                    };
                    execution.verify_result = .{
                        .success = false,
                        .message = @errorName(err),
                    };
                    try apply_results.append(failure);
                    summary.failure_count += 1;
                    failure_seen = true;
                    if (self.config.stop_on_first_failure) break;
                    summary.phase = .applying;
                    continue;
                };

                execution.verify_result = verify;
                if (!verify.success) {
                    const failure = types.ApplyResult{
                        .domain_id = execution.contract.plan.domain_id,
                        .success = false,
                        .message = verify.message,
                        .stats = .{ .failed = 1 },
                        .duration_ms = 0,
                    };
                    try apply_results.append(failure);
                    summary.failure_count += 1;
                    failure_seen = true;
                    if (self.config.stop_on_first_failure) break;
                }
                summary.phase = .applying;
            }
        }

        var reverted_domains: u32 = 0;
        var outcome: TransactionOutcome = .success;

        if (failure_seen and self.config.rollback_on_failure and applied_count > 0) {
            summary.phase = .reverting;
            reverted_domains = try self.rollbackApplied(ctx, executions);
            outcome = .rolled_back;
        } else if (failure_seen and summary.success_count > 0) {
            outcome = .partial_success;
        } else if (failure_seen) {
            outcome = .failed;
        } else {
            outcome = .success;
        }

        summary.phase = switch (outcome) {
            .success => .committed,
            .partial_success => .failed,
            .rolled_back => .rolled_back,
            .failed => .failed,
        };
        summary.finished_at_ms = ctx.now_ms;

        return .{
            .summary = summary,
            .outcome = outcome,
            .apply_results = try apply_results.toOwnedSlice(),
            .reverted_domains = reverted_domains,
        };
    }

    fn probeAll(
        self: *Engine,
        ctx: *const types.DomainContext,
        executions: []DomainExecution,
    ) EngineError!void {
        _ = self;

        for (executions) |*execution| {
            if (!execution.contract.plan.enabled) {
                execution.probe_result = .{
                    .support_level = .partial,
                    .message = "disabled by plan",
                };
                continue;
            }

            execution.probe_result = execution.contract.vtable.probe(ctx) catch |err| {
                return err;
            };
        }
    }

    fn captureBaselines(
        self: *Engine,
        ctx: *const types.DomainContext,
        executions: []DomainExecution,
    ) EngineError!void {
        _ = self;

        for (executions) |*execution| {
            if (!execution.contract.plan.enabled) continue;
            if (!execution.contract.plan.reversible) continue;

            execution.baseline = execution.contract.vtable.capture_baseline(ctx) catch |err| {
                return err;
            };
        }
    }

    fn rollbackApplied(
        self: *Engine,
        ctx: *const types.DomainContext,
        executions: []DomainExecution,
    ) EngineError!u32 {
        _ = self;

        var reverted: u32 = 0;
        var i = executions.len;
        while (i > 0) {
            i -= 1;
            var execution = &executions[i];
            if (!execution.attempted_apply) continue;

            const apply_result = execution.apply_result orelse continue;
            if (!apply_result.success) continue;
            if (!execution.contract.plan.reversible) continue;

            execution.contract.vtable.revert(
                ctx,
                if (execution.baseline) |*baseline| baseline else null,
            ) catch {
                continue;
            };

            execution.reverted = true;
            reverted += 1;
        }

        return reverted;
    }

    fn validatePlans(plans: []const types.DomainContract) EngineError!void {
        for (plans, 0..) |plan, i| {
            var j: usize = i + 1;
            while (j < plans.len) : (j += 1) {
                if (plans[j].plan.domain_id == plan.plan.domain_id) {
                    return error.DuplicateDomain;
                }
            }
        }
    }
};

fn stubProbe(_: *const types.DomainContext) anyerror!types.ProbeResult {
    return .{
        .support_level = .supported,
        .message = "ok",
    };
}

fn stubCaptureBaseline(ctx: *const types.DomainContext) anyerror!?types.BaselineBlob {
    return try types.BaselineBlob.initCopy(ctx.allocator, "baseline");
}

fn stubApply(_: *const types.DomainContext, _: ?*const types.BaselineBlob) anyerror!types.ApplyResult {
    return .{
        .domain_id = .memory_optimizer,
        .success = true,
        .message = "applied",
        .stats = .{ .optimized = 1 },
        .duration_ms = 1,
    };
}

fn stubVerify(_: *const types.DomainContext) anyerror!types.VerifyResult {
    return .{
        .success = true,
        .message = "verified",
    };
}

fn stubRevert(_: *const types.DomainContext, _: ?*const types.BaselineBlob) anyerror!void {}

test "engine rejects duplicate domains" {
    const allocator = std.testing.allocator;
    var engine = Engine.init(allocator, .{});

    const contract = types.DomainContract{
        .plan = .{
            .domain_id = .memory_optimizer,
        },
        .vtable = .{
            .probe = stubProbe,
            .capture_baseline = stubCaptureBaseline,
            .apply = stubApply,
            .verify = stubVerify,
            .revert = stubRevert,
        },
    };

    const ctx = types.DomainContext{
        .allocator = allocator,
        .now_ms = 1,
        .policy = &types.AppPolicy{},
        .trigger_reason = .manual,
    };

    try std.testing.expectError(
        error.DuplicateDomain,
        engine.execute(&ctx, &.{ contract, contract }),
    );
}

test "engine executes simple successful transaction" {
    const allocator = std.testing.allocator;
    var engine = Engine.init(allocator, .{});

    const contract = types.DomainContract{
        .plan = .{
            .domain_id = .memory_optimizer,
        },
        .vtable = .{
            .probe = stubProbe,
            .capture_baseline = stubCaptureBaseline,
            .apply = stubApply,
            .verify = stubVerify,
            .revert = stubRevert,
        },
    };

    const policy = types.AppPolicy{};
    const ctx = types.DomainContext{
        .allocator = allocator,
        .now_ms = 42,
        .policy = &policy,
        .trigger_reason = .manual,
    };

    var report = try engine.execute(&ctx, &.{contract});
    defer report.deinit(allocator);

    try std.testing.expectEqual(TransactionOutcome.success, report.outcome);
    try std.testing.expectEqual(@as(u32, 1), report.summary.success_count);
    try std.testing.expectEqual(@as(usize, 1), report.apply_results.len);
}
