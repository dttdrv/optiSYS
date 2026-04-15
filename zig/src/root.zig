 //! Root module for the non-UI `optiSYS` Zig core.
 //!
 //! This module intentionally exposes only backend/core building blocks.
 //! UI integration should depend on these APIs rather than owning system logic.

 pub const std = @import("std");

 pub const core = struct {
     pub const types = @import("core/types.zig");
     pub const errors = @import("core/errors.zig");
     pub const config = @import("core/config.zig");
     pub const telemetry = @import("core/telemetry.zig");
     pub const engine = @import("core/engine.zig");
 };

 pub const platform = struct {
     pub const windows = struct {
         pub const kernel32 = @import("platform/windows/kernel32.zig");
         pub const powrprof = @import("platform/windows/powrprof.zig");
         pub const psapi = @import("platform/windows/psapi.zig");
         pub const advapi32 = @import("platform/windows/advapi32.zig");
         pub const user32 = @import("platform/windows/user32.zig");
         pub const types = @import("platform/windows/types.zig");
     };
 };

 pub const modules = struct {
     pub const power = @import("modules/power/mod.zig");
     pub const memory = @import("modules/memory/mod.zig");
     pub const process = @import("modules/process/mod.zig");
     pub const policy = @import("modules/policy/mod.zig");
     pub const persistence = @import("modules/persistence/mod.zig");
 };

 pub const app = struct {
     pub const runtime = @import("app/runtime.zig");
 };

 test {
     _ = core;
     _ = platform;
     _ = modules;
     _ = app;
 }
