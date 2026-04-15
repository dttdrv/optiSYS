 //! Power module exports for the non-UI `optiSYS` Zig core.
 //!
 //! This module groups all power-related backend functionality behind a small,
 //! stable namespace so the rest of the backend can depend on one import path.
 //! The implementation files are intentionally split by responsibility:
 //! - `power.zig` owns the high-level power module facade
 //! - `monitor.zig` owns event-driven and fallback polling logic
 //! - `battery_info.zig` owns battery telemetry shaping and confidence helpers
 //! - `controller.zig` owns power-oriented optimization actions and policies

 pub const PowerModule = @import("power.zig").PowerModule;
 pub const PowerModuleConfig = @import("power.zig").PowerModuleConfig;
 pub const PowerError = @import("power.zig").PowerError;
 pub const PowerSnapshot = @import("power.zig").PowerSnapshot;

 pub const PowerMonitor = @import("monitor.zig").PowerMonitor;
 pub const PowerMonitorConfig = @import("monitor.zig").PowerMonitorConfig;
 pub const PowerMonitorEvent = @import("monitor.zig").PowerMonitorEvent;

 pub const BatteryTelemetry = @import("battery_info.zig").BatteryTelemetry;
 pub const BatteryTelemetryConfig = @import("battery_info.zig").BatteryTelemetryConfig;
 pub const BatteryConfidence = @import("battery_info.zig").BatteryConfidence;

 pub const PowerController = @import("controller.zig").PowerController;
 pub const PowerControllerConfig = @import("controller.zig").PowerControllerConfig;
 pub const PowerAction = @import("controller.zig").PowerAction;
 pub const PowerActionResult = @import("controller.zig").PowerActionResult;

 test {
     _ = PowerModule;
     _ = PowerModuleConfig;
     _ = PowerError;
     _ = PowerSnapshot;

     _ = PowerMonitor;
     _ = PowerMonitorConfig;
     _ = PowerMonitorEvent;

     _ = BatteryTelemetry;
     _ = BatteryTelemetryConfig;
     _ = BatteryConfidence;

     _ = PowerController;
     _ = PowerControllerConfig;
     _ = PowerAction;
     _ = PowerActionResult;
 }
