 //! Shared error definitions for the non-UI `optiSYS` Zig core.
 //!
 //! These errors are intentionally grouped by subsystem so the backend can:
 //! - keep failure modes explicit
 //! - avoid broad, unstructured global errors
 //! - compose subsystem errors into higher-level application errors
 //! - stay suitable for transactional optimization and recovery flows

 const std = @import("std");

 /// Common low-level errors that can occur across multiple subsystems.
 pub const CommonError = error{
     AccessDenied,
     AlreadyInitialized,
     AlreadyRunning,
     Busy,
     CorruptData,
     InvalidArgument,
     InvalidState,
     IoFailure,
     NotFound,
     NotInitialized,
     NotRunning,
     NotSupported,
     OutOfMemory,
     PermissionDenied,
     PlatformFailure,
     SerializationFailure,
     DeserializationFailure,
     Timeout,
     Unexpected,
 };

 /// Errors related to configuration loading, validation, and persistence.
 pub const ConfigError = CommonError || error{
     InvalidConfig,
     MissingRequiredField,
     UnsupportedConfigVersion,
 };

 /// Errors related to telemetry collection and interpretation.
 pub const TelemetryError = CommonError || error{
     CounterUnavailable,
     SampleUnavailable,
     ConfidenceTooLow,
 };

 /// Errors related to power and battery monitoring.
 pub const PowerError = CommonError || TelemetryError || error{
     BatteryNotPresent,
     PowerNotificationUnavailable,
     PowerStatusUnavailable,
     UnsupportedPowerSetting,
 };

 /// Errors related to memory monitoring and memory pressure handling.
 pub const MemoryError = CommonError || TelemetryError || error{
     MemoryNotificationUnavailable,
     MemoryStatusUnavailable,
     OptimizationIneffective,
     PressureSignalUnavailable,
 };

 /// Errors related to process enumeration, classification, and tuning.
 pub const ProcessError = CommonError || error{
     OpenProcessFailed,
     ProcessExited,
     ProcessEnumerationFailed,
     ProcessInformationUnavailable,
     UnsupportedProcessOperation,
 };

 /// Errors related to service control and service state transitions.
 pub const ServiceError = CommonError || error{
     OpenServiceManagerFailed,
     OpenServiceFailed,
     QueryServiceFailed,
     ServiceStartFailed,
     ServiceStopFailed,
     ServiceStateTimeout,
     UnsupportedServiceOperation,
 };

 /// Errors related to persistence stores and crash recovery state.
 pub const PersistenceError = CommonError || error{
     AtomicWriteFailed,
     RecoveryStateInvalid,
     SnapshotMissing,
     StoreUnavailable,
 };

 /// Errors related to policy evaluation and optimization planning.
 pub const PolicyError = CommonError || error{
     CooldownActive,
     ForegroundProtectionActive,
     HysteresisNotSatisfied,
     NoEligibleActions,
     NoTriggerMatched,
     PolicyRejected,
 };

 /// Errors related to transactional optimization execution.
 pub const TransactionError = CommonError || error{
     ApplyFailed,
     BaselineCaptureFailed,
     CommitFailed,
     ProbeFailed,
     RevertFailed,
     RollbackFailed,
     VerificationFailed,
 };

 /// Errors related to Windows platform interop wrappers.
 pub const PlatformError = CommonError || error{
     ApiCallFailed,
     HandleCreationFailed,
     InvalidHandle,
     Win32Failure,
 };

 /// Top-level core error set used by orchestration code.
 ///
 /// This should be used sparingly at the application boundary. Lower layers should
 /// prefer narrower subsystem-specific error sets whenever practical.
 pub const CoreError =
     ConfigError ||
     TelemetryError ||
     PowerError ||
     MemoryError ||
     ProcessError ||
     ServiceError ||
     PersistenceError ||
     PolicyError ||
     TransactionError ||
     PlatformError;

 /// Returns a stable string name for a core error.
 pub fn name(err: anyerror) []const u8 {
     return @errorName(err);
 }

 /// Returns true when the error is typically transient and a retry may succeed.
 pub fn isTransient(err: anyerror) bool {
     return switch (err) {
         error.Busy,
         error.Timeout,
         error.ProcessExited,
         error.SampleUnavailable,
         error.MemoryNotificationUnavailable,
         error.PowerNotificationUnavailable,
         error.ServiceStateTimeout,
         error.CooldownActive,
         error.HysteresisNotSatisfied,
         error.ForegroundProtectionActive,
         => true,

         else => false,
     };
 }

 /// Returns true when the error indicates the current platform or environment
 /// does not support the requested capability.
 pub fn isUnsupported(err: anyerror) bool {
     return switch (err) {
         error.NotSupported,
         error.UnsupportedConfigVersion,
         error.UnsupportedPowerSetting,
         error.UnsupportedProcessOperation,
         error.UnsupportedServiceOperation,
         => true,

         else => false,
     };
 }

 /// Returns true when the error suggests the backend should fall back to a
 /// degraded or read-only mode rather than continue normal optimization.
 pub fn requiresDegradedMode(err: anyerror) bool {
     return switch (err) {
         error.AccessDenied,
         error.PermissionDenied,
         error.PlatformFailure,
         error.Win32Failure,
         error.ApiCallFailed,
         error.HandleCreationFailed,
         error.InvalidHandle,
         error.StoreUnavailable,
         => true,

         else => false,
     };
 }

 test "error names are stable strings" {
     try std.testing.expectEqualStrings("Timeout", name(error.Timeout));
     try std.testing.expectEqualStrings("PolicyRejected", name(error.PolicyRejected));
 }

 test "transient classification matches expected retryable errors" {
     try std.testing.expect(isTransient(error.Timeout));
     try std.testing.expect(isTransient(error.CooldownActive));
     try std.testing.expect(!isTransient(error.AccessDenied));
     try std.testing.expect(!isTransient(error.InvalidArgument));
 }

 test "unsupported classification matches capability failures" {
     try std.testing.expect(isUnsupported(error.NotSupported));
     try std.testing.expect(isUnsupported(error.UnsupportedProcessOperation));
     try std.testing.expect(!isUnsupported(error.Timeout));
 }

 test "degraded mode classification matches platform and permission failures" {
     try std.testing.expect(requiresDegradedMode(error.AccessDenied));
     try std.testing.expect(requiresDegradedMode(error.Win32Failure));
     try std.testing.expect(!requiresDegradedMode(error.CooldownActive));
     try std.testing.expect(!requiresDegradedMode(error.NoEligibleActions));
 }
