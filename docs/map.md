# Map

Two programs run. Everything else is a library. The solution has no runtime or build dependency on `core.dll` or `runtime/legacy`.

| Project | Role |
| --- | --- |
| `Mmopal.Desktop` | WPF window. Starts and supervises the worker (Windows Job Object + unique `CurrentUserOnly` pipe per launch) and sends start, pause, resume, stop. |
| `Mmopal.Worker` | Session process. Owns the trading loop, candidate loading, screen observation, and the SQLite checkpoint database. |
| `Mmopal.Contracts` | Commands, events, vision interfaces, and shared records. |
| `Mmopal.Gw2` | Trading rules, 1-copper-floor fees, paginated official GW2 API client, candidate/blacklist file loader, and observation/replay readers. |
| `Mmopal.Infrastructure` | Paths, options, logging, SQLite checkpoint store, delimited parser, and optional licensing (disabled by default). |
| `Mmopal.Native` | Owned Win32 screen capture, OpenCvSharp4 template matcher, Windows OCR reader, and unwired Win32 input controller. |
| `tests/Mmopal.Domain.Tests` | xUnit tests covering domain rules, pagination, controller deduplication, worker health/pause, vision/capture, and WPF smoke test. |

`assets/templates/tradingpost` holds the four Trading Post UI template PNGs copied into the worker output directory. `runtime/legacy` stays on disk only as an unreferenced historical archive.

Start with [run.md](run.md) when launching the app. [panel-checklist.md](panel-checklist.md) tracks the old window's operator surface. [core-behavior.md](core-behavior.md) and [legacy-bridge.md](legacy-bridge.md) record the completed migration away from `core.dll`.
