 //! Memory module exports for the non-UI `optiSYS` Zig core.
 //!
 //! This module groups memory-related backend functionality behind a small,
 //! stable namespace so the rest of the application can depend on one import
 //! path while the implementation evolves.
 //!
 //! Current scope:
 //! - memory telemetry and pressure monitoring
 //! - memory optimization planning/execution
 //! - shared memory-domain types and errors
 //!
 //! The concrete implementations are intentionally split into focused files.

 pub const memory = @import("memory.zig");
 pub const monitor = @import("monitor.zig");
 pub const optimizer = @import("optimizer.zig");
 pub const pressure = @import("pressure.zig");

 pub const MemoryModule = memory.MemoryModule;
 pub const MemoryModuleConfig = memory.MemoryModuleConfig;
 pub const MemorySnapshot = memory.MemorySnapshot;
 pub const MemoryPressureLevel = memory.MemoryPressureLevel;
 pub const MemoryError = memory.MemoryError;

 test {
     _ = memory;
     _ = monitor;
     _ = optimizer;
     _ = pressure;
 }
