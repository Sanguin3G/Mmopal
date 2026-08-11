# Architecture

The modernized application has a WPF desktop process and a separate automation worker.

```text
Mmopal.Desktop ── named pipe ── Mmopal.Worker
                                  │
                                  ├─ Mmopal.Gw2
                                  ├─ Mmopal.Native
                                  ├─ Mmopal.Infrastructure
                                  └─ Mmopal.Licensing
```

The desktop process owns operator controls and worker supervision. The worker owns game interaction and can be restarted without taking down the UI. Game input is serialized through one executor. API polling, persistence, and telemetry may run independently, but no two input actions run concurrently.

The old Lua/native bundle is not deleted or treated as rebuildable source. It is a behavioral reference until a native compatibility adapter and replay suite prove parity.
