const std = @import("std");

pub fn build(b: *std.Build) void {
    const target = b.standardTargetOptions(.{});
    const optimize = b.standardOptimizeOption(.{});

    // ── Core library (importable Zig module) ──────────────────────────
    const core_mod = b.addModule("optisys_core", .{
        .root_source_file = b.path("src/root.zig"),
        .target = target,
        .optimize = optimize,
    });

    // ── CLI executable (for testing and scripting) ────────────────────
    const exe = b.addExecutable(.{
        .name = "optisys-core",
        .root_module = b.addModule("optisys_cli", .{
            .root_source_file = b.path("src/main.zig"),
            .target = target,
            .optimize = optimize,
        }),
    });
    exe.linkLibC();
    exe.subsystem = .Console;

    b.installArtifact(exe);

    const run_cmd = b.addRunArtifact(exe);
    if (b.args) |args| {
        run_cmd.addArgs(args);
    }

    const run_step = b.step("run", "Run the optiSYS CLI");
    run_step.dependOn(&run_cmd.step);

    // ── Shared library (.dll) for .NET P/Invoke interop ──────────────
    const dll_mod = b.addModule("optisys_bridge", .{
        .root_source_file = b.path("src/c_api/bridge.zig"),
        .target = target,
        .optimize = optimize,
    });

    const dll = b.addSharedLibrary(.{
        .name = "optisys_core",
        .root_module = dll_mod,
    });
    dll.linkLibC();

    b.installArtifact(dll);

    // ── Tests ─────────────────────────────────────────────────────────
    const unit_tests = b.addTest(.{
        .root_module = core_mod,
    });

    const run_unit_tests = b.addRunArtifact(unit_tests);

    const test_step = b.step("test", "Run unit tests");
    test_step.dependOn(&run_unit_tests.step);

    // ── Check step (compile without install) ──────────────────────────
    const check_exe = b.addExecutable(.{
        .name = "optisys-core-check",
        .root_module = core_mod,
    });
    check_exe.linkLibC();

    const check_step = b.step("check", "Compile without installing");
    check_step.dependOn(&check_exe.step);

    b.default_step.dependOn(&exe.step);
}