 //! Persistence module exports for the non-UI `optiSYS` Zig core.
 //!
 //! This module groups backend persistence concerns behind a small, stable
 //! namespace so the rest of the core can depend on persistence contracts
 //! without knowing file layout details.
 //!
 //! Current scope:
 //! - settings storage
 //! - snapshot storage
 //! - crash recovery state
 //! - high-level store facade
 //!
 //! Design goals:
 //! - keep persistence host-agnostic
 //! - prefer atomic, corruption-tolerant behavior
 //! - make recovery explicit and testable
 //! - avoid UI ownership of settings or snapshot lifecycle

 pub const persistence = @import("persistence.zig");
 pub const settings_store = @import("settings_store.zig");
 pub const snapshot_store = @import("snapshot_store.zig");
 pub const recovery = @import("recovery.zig");

 pub const Store = persistence.Store;
 pub const StoreConfig = persistence.StoreConfig;
 pub const PersistenceError = persistence.PersistenceError;

 test {
     _ = persistence;
     _ = settings_store;
     _ = snapshot_store;
     _ = recovery;
 }
