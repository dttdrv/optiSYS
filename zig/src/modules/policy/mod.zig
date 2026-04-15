 //! Policy module exports for the non-UI `optiSYS` Zig core.
 //!
 //! This module groups policy-related building blocks behind a stable import
 //! surface so the rest of the backend can depend on `modules.policy` without
 //! caring about file layout details.

 pub const policy = @import("policy.zig");
 pub const hysteresis = @import("hysteresis.zig");

 pub const PolicyEngine = policy.PolicyEngine;
 pub const PolicyError = policy.PolicyError;
 pub const OptimizationDecision = policy.OptimizationDecision;
 pub const OptimizationReason = policy.OptimizationReason;
 pub const OptimizationRequest = policy.OptimizationRequest;

 pub const HysteresisState = hysteresis.HysteresisState;
 pub const HysteresisConfig = hysteresis.HysteresisConfig;
 pub const HysteresisDecision = hysteresis.HysteresisDecision;

 test {
     _ = policy;
     _ = hysteresis;
 }
