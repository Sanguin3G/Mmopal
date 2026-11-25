# Mmopal — project guide

Guild Wars 2 Trading Post companion: a .NET 10 WPF desktop app supervises a worker process that reads the
GW2 window (template matching, digit shapes, Windows OCR) and the official GW2 API, and places/cancels/lists
orders through bounded clicks and keystrokes. Live input only happens in Live mode with **Arm live input** on.

## Map

| Project | What lives there |
| --- | --- |
| `src/Mmopal.Contracts` | Shared records: transactions, frames, worker messages, portfolio rows. |
| `src/Mmopal.Gw2` | Trading logic, no UI. `Api/` GW2 API client · `Market/` scanner, live re-pricing, order book · `Trading/` cycle controller, decisions, limits, adaptive rules (bot-war pause, relist policy), sell queue, character stock (swap target) · `Portfolio/` income analytics, insights, advisor, Auto-Tune · `Settings/` client/operator/report settings. |
| `src/Mmopal.Worker` | The worker process. `Runtime/` session loop (`WorkerRuntime` + partials: Session (setup and report), Cycle (the loop, one method per step), Commands, Status, ShoppingList, Inventory, Recovery) and the named-pipe server · `TradingPost/` the screen driver (`Gw2TradingPostUiDriver` + partials: Launcher, Characters, TradingPostWindow, Buying, Selling, PriceReading, Search, Transactions) · `Vision/` digit-shape price reader and the drawn-name reader · `Client/` game launch/restart. |
| `src/Mmopal.Native` | Win32 and OS access. `Screen/` capture, OpenCV template matching, Windows OCR, display health · `Control/` input, key names, game window and process, keep-awake · `Secrets/` DPAPI secrets and the API key vault · interfaces in `NativeAbstractions.cs`. |
| `src/Mmopal.Infrastructure` | SQLite stores (checkpoints, ledger, session reports, session metrics), paths, logging. |
| `src/Mmopal.Desktop` | WPF app: `MainWindow` shell (sidebar, navigation, responsive layout, worker events) and one UserControl per page in `Pages/` (Session, Income, Reports, Scanner, Lists, Settings, Guide). `MainWindow.FindName` also searches the pages, which the smoke tests rely on. |
| `tests/Mmopal.Domain.Tests` | xUnit tests, including WPF smoke tests and real GW2 dialog crops in `Fixtures/coin-frames`. |
| `assets/templates/tradingpost` | Template PNGs, calibrated on 1024×768 frames. |
| `docs/` | `run.md` (setup, going live), `trading-strategy.md` (rules, Auto-Tune), `gw2-trading-controller.md` (live flows, log lines), `architecture.md` (processes, replay payload), `migration.md` (what came from the original Mmopal, what replaced it, what is left). Each fact lives in one of them; keep them short. |

Folders under `Mmopal.Gw2`, `Mmopal.Worker` and `Mmopal.Native` are for reading only: types keep the project namespace
(`Mmopal.Gw2`, `Mmopal.Worker`, `Mmopal.Native`), except `Mmopal.Worker.Vision`.

## Build and test

```
dotnet test Mmopal.sln -p:UseSharedCompilation=false --nologo
dotnet build Mmopal.sln -p:UseSharedCompilation=false -m:1 --nologo
```

- If Mmopal is running, its DLLs in `bin/` are locked: build and test with `--artifacts-path <temp folder>` and only
  build into `bin/` after the app is closed.
- Tests must stay green; the desktop smoke tests construct `MainWindow` and look controls up by `x:Name`.

## Working rules

- Never commit `.codex/`. No agent credit in commits or files: no `Co-Authored-By:` or "generated with" lines.
- Keep `docs/` and the in-app Guide in step with behaviour changes.
- Live-session evidence lives in `%LOCALAPPDATA%\Mmopal\logs\mmopal-YYYYMMDD.jsonl` (UTC), the SQLite DB
  `%LOCALAPPDATA%\Mmopal\mmopal.db` (ledger, equity snapshots, `cycle_metrics` and `session_events` per live session) and `screenshots\` (diagnostic frames). Analyse
  copies of the DB, never the live file.
- Safety first in the driver: a price is only typed after it is read and checked against the API, and every
  typed price is confirmed on the dialog Total before Place Order / List This Item. When a read is uncertain the
  code aborts or falls back; it never guesses.
- Every key also reaches the game when no text box has the focus (Backspace = Reply, Enter = chat, letters = movement and
  skills). Type or press keys only with the Trading Post proven on screen, and never click a guessed position first.
- Comments explain *why*, usually with the live measurement behind a rule (e.g. "41 min lost in a 7 h session").
  Keep them when refactoring.
