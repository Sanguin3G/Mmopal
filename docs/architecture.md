# Architecture

The modernized application has a WPF desktop process and a separate automation worker.

```text
Mmopal.Desktop ── CurrentUserOnly named pipe ── Mmopal.Worker
                                                  │
                                                  ├─ Mmopal.Gw2
                                                  ├─ Mmopal.Native
                                                  └─ Mmopal.Infrastructure
```

The desktop process owns operator controls and worker supervision (`DesktopWorkerSupervisor`). Each worker launch uses a fresh per-instance named pipe restricted to the current Windows user (`PipeOptions.CurrentUserOnly`), assigns the child worker to a Windows Job Object (`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`), and passes `--parent-pid` so the worker exits cleanly if the desktop terminates unexpectedly.

The worker owns market polling, screen observation, trading decisions, and SQLite checkpointing. Game input is not armed; `Win32InputController` stays unwired in `Mmopal.Native`.

The legacy `core.dll` bridge (`Mmopal.LegacyBridge`) has been removed. Screen capture, template matching, OCR, and Guild Wars 2 account data all run natively in .NET 10.
