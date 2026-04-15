 //! Process module exports for the non-UI `optiSYS` Zig core.
 //!
 //! This module groups process-related backend functionality behind a stable
 //! import surface so higher layers can depend on `modules.process` without
 //! knowing the internal file layout.
 //!
 //! The process subsystem is responsible for:
 //! - process enumeration
 //! - process snapshotting
 //! - candidate filtering and ranking
 //! - foreground-aware protection helpers
 //! - future process-level optimization actions

 pub const process = @import("process.zig");
 pub const snapshot = @import("snapshot.zig");
 pub const classifier = @import("classifier.zig");
 pub const filters = @import("filters.zig");

 pub const ProcessModule = process.ProcessModule;
 pub const ProcessModuleConfig = process.ProcessModuleConfig;
 pub const ProcessSnapshot = process.ProcessSnapshot;
 pub const ProcessCandidate = process.ProcessCandidate;
 pub const ProcessError = process.ProcessError;

 test {
     _ = process;
     _ = snapshot;
     _ = classifier;
     _ = filters;
 }
