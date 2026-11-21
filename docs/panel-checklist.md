# Panel checklist

This is a checklist of the old operator window, not a port. The new WPF window picks items from the "Still missing" list only when a later pass still wants them.

REA inspected the old `mmopal.dll` (from the former `runtime/legacy` folder, since deleted) without loading or running it. The assembly is `mmopal` 1.0.0.0, modern .NET CIL, AnyCPU. The window type is `MMOPal.UI.MainWindow`. Member coverage was complete. SHA-256 `866f5cb83697779dbf68c3d844686f74271751093e8b535973187bd98eb4c94d`. Evidence `ev_9a81560ac91d9fc39ac0558b10129bc9c7f042e2633cd19d6aafe898e8ac981b` (identity) and `ev_442483226605b5c1730b5f6cb972550b079c3263a586e6ea230119aa0752d302` (members).

## Already in the new window

- Modern 6-view sidebar layout (**Session**, **Income & Portfolio**, **Market Scanner**, **Lists & Blacklist**, **Settings**, **Guide & Setup**) with full Dark & Light mode templates (including native Windows DWM dark title bar, themed `ScrollBar`/`TabControl`/`Expander`, and explicit window background) and vector icons.
- Start, pause, resume, stop, and emergency stop for one session with dynamic button state indicators (`Running...` / `Paused` / selector locking) and button confirmation feedback (`✓ Saved`, `✓ Tuned`, `✓ Snapped`, `✓ Synced`, `✓ Copied`) — plus system-wide Win32 **Global Hotkeys** (`Ctrl+Shift+P`, `Ctrl+Shift+S`, `Ctrl+Shift+X`), configurable **Max Session Duration Cap** (default 10h), and live **Global TP Cycle & Reset Countdown** pill.
- Trading Post as the activity. Fishing is a scaffold and issues no input.
- Dry run, observe only, and **Live (Armed trading)**: explicit **Arm live input** checkbox interlock + Safe/Armed pill, executing bounded OpenCV template-driven flows (`EnsureTradingPostOpenAsync`, `CollectDeliveryBoxAsync`, `SellInventoryPassAsync`, free `CancelOutbidBuyOrdersAsync`, `PlaceBuyOrderAsync`, and `RotateParkedCharacterAsync`) across all 63 ported TP templates, plus `0g` / reserve-floor and running-spendable-budget buy guards.
- Replay data, **Screen observe** (owned capture/OpenCV/Windows OCR + official GW2 API), optional JSON/CSV candidates & blacklist paths, optional JSONL fixture, recording, and frame capture inside a collapsed **Advanced** expander.
- **Built-in Per-Account Income & Portfolio Tracker**: active account indicator badge & sidebar **Active Account** switcher, fixed full-width timeframe selector, permanently archived SQLite ledger past the 90-day API cutoff isolated by `account_name`, exact FIFO matched profit, 90%-net active sell equity, daily & local-hour activity charts, **Active Orders** health (`Top of Book` / `Outbid` / `Undercut`), and CSV export.
- **Built-in Market Scanner**: queries the official GW2 API (`v2/commerce/prices`, `v2/commerce/listings`, and SQLite-cached `v2/items`) with strategy presets, **Auto-Tune from Wallet**, instant **Re-Filter Cached**, Max ROI guard, item category filter, order-book wall-depth signal badges, fee-breakdown tooltips, optional DataWars2 24h volume, plus GW2BLTC / GW2Profits CSV & JSON import and one-click export to session candidates or custom list.
- **Guide & Setup**: live auto-checked **Pre-Flight Readiness Checklist** (API key, `.exe`-verified GW2 install, Windows 100% DPI scaling, candidates) with auto-snap clarity (`Resolution = Window`), 4-step walkthrough, and visual 4-tile live-highlighted local Market Schedule + 4 compact Flipping Rule cards.
- **Lists & Blacklist editor**: visual Custom Shopping List and Blacklist editors with Add, Remove, CSV/JSON import, and CSV export, automatically included in sessions when enabled.
- **Dedicated Settings view**: GW2Efficiency-inspired **API Keys** DPAPI multi-account table under **Accounts & API** (Use/Active, ↑/↓ reorder, VALID/INVALID status, account, masked key + copy icon, editable name, delete icon, Save Names, Re-verify, ArenaNet Key Page, Add & Verify with legacy key migration), smart `.exe`-only GW2 client detector, **Network & Ping Latency Profile** (`Low`, `Normal`, `HighPing` 7500ms/900ms/60s for 220–280ms SEA→EU without Mudfish, `Custom`) with per-request API timeouts, trading limits & **Auto-Tune** (wallet-sized limits; leaves session cap and swap interval alone), parked character count/keybinds, AFK character swap interval + jitter, global hotkeys, and collapsed Advanced paths.
- **Launch & snap GW2** (`1024×768` at top-left `(0, 0)`), plus optional auto-snap / fail-fast auto-launch on Screen observe start.
- Live KPI health cards (Status, Observation/UI, Visible Price & Order Book Summary with low-gold pause indicator, Timing, AFK Swap & Session Cap countdown) and human-readable **Activity Log** with **Raw JSON** toggle, Clear, and event count.

## Still missing

These are the remaining GW2 controls the old window exposed. Add them only if you still want them.

### Settings

- Price mode toggle (`api` vs `ocr`) and validate current transaction prices toggle.
- Gold threshold for relist and sell-only toggle.
- Seconds before canceling sale items, and seconds before canceling ordered items.
- Iterations before selling, restarting the Trading Post, reselling, and getting info.

Do not put a GW2 API key into git. Save your key in the **Settings** tab (encrypted via Windows DPAPI) or set `MMOPAL_Native__Gw2ApiKey`.

### Lists and transactions

- Full per-order Current Transactions grid is now in **Income & Portfolio → Active Orders** (live-refreshed on **Sync GW2 Account**, alongside summarized status on the Session health card).

### Log

- File tailing of historical `logs/*.log` with level filter (live session log with clear & counter is in the Session view).

### Left out on purpose

Albion, OSRS, and RS3 settings, account grids, and shopping lists stay archived. The license key box wrote `config.ini`; the new licensing module is separate and disabled. `core.dll` and `Mmopal.LegacyBridge` are completely removed.

## Evidence limits

REA read metadata and `ldstr` literals. It did not execute the window, click a grid, or watch a log file. A setting name on `MainWindow` means the old window had that control. It does not by itself prove the Lua bot honored the saved value.