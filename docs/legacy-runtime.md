# Legacy runtime migration

The original Lua/native package is preserved under `runtime/legacy`.

The new .NET worker deliberately starts in dry-run mode. Native direct-call compatibility must be validated before live mode is enabled. The migration sequence is:

1. Inspect the native exports and host loading behavior.
2. Build a no-input screenshot/process probe.
3. Reproduce decisions in replay tests.
4. Add verified input actions behind the native interfaces.
5. Enable live mode only after action verification and recovery tests pass.
