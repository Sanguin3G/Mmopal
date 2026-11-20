# Run

Build and start the WPF window. The window starts and supervises the worker process itself (bound via a Windows Job Object and `--parent-pid` watchdog, over a `CurrentUserOnly` named pipe). Tests never open Guild Wars 2.

## Build

Use Rider with `Mmopal.Desktop` as the startup project, or:

```powershell
dotnet build Mmopal.sln
dotnet run --project src/Mmopal.Desktop/Mmopal.Desktop.csproj
```

`dotnet test Mmopal.sln` runs the xUnit suite. One test exercises `WindowsDesktopScreenshotProvider` on a 4×4 desktop region and one test shows the window on a background thread through a stand-in supervisor. The suite does not open Guild Wars 2 or send input.

## First session

1. Open **Guide & Setup** to review the auto-checked **Pre-Flight Readiness Checklist** (API key, GW2 path, Windows 100% display scale, and flip list), local-timezone Market Schedule cards, and 4 compact flipping rules.
2. Leave **Data source** on **Replay data** and **Run mode** on **Dry run** in the **Session** view, then click **Try offline sample**. The activity log shows a clean human-readable summary by default (toggle **Raw JSON** to inspect payloads). No game window is required.
3. To find real profitable flips, open **Market Scanner**, pick a preset (such as **Balanced** or **Auto-Tune from Wallet**), click **Scan GW2 Market** (queries `v2/commerce/prices`, `v2/commerce/listings`, and cached `v2/items`, with optional 24h DataWars2 volume or GW2BLTC / GW2Profits CSV import), then click **Use All for Session** or **+ Custom List**.
4. Open **Income & Portfolio** and click **Sync GW2 Account** to permanently archive transactions into SQLite per active GW2 account past ArenaNet's 90-day cutoff, compute exact FIFO realized flip profit, inspect 90%-net active sell equity, and see which open orders are **Top of Book**, **Outbid**, or **Undercut**. Switch accounts anytime via the sidebar **Active Account** dropdown or the **Manage API Keys** table in **Settings**.
5. Manage your custom targets and blocked items in **Lists & Blacklist**, and configure multi-account keys, smart GW2 client detect (ignoring Steam `.ico` files) & auto-snap (`1024×768`), **Network & Ping Latency Profile** (`High Ping / SEA→EU 200–300 ms` with 7500ms UI wait / 900ms settle / 60s API timeout), `0g` / reserve floor buy pause, 10h session duration cap, parked character count/keybinds, and global hotkeys (`Ctrl+Shift+P` / `Ctrl+Shift+S` / `Ctrl+Shift+X`) in **Settings**.

**Observe only** reports live detection without sending input. **Live (Armed trading)** executes bounded Trading Post clicks/keys only when **Arm live input** is explicitly checked on the Session toolbar (otherwise halts safely in `NeedsAttention`); buys automatically pause whenever wallet balance hits `0g` or your gold floor while delivery collection, inventory selling, and free outbid buy refreshes remain active. **Fishing** stays a scaffold and issues no input.

## Screen observe & GW2 API keys

**Screen observe** reads the screen with owned Win32 capture (`WindowsDesktopScreenshotProvider`), OpenCV template search (`FrameTemplateMatcher`), and Windows OCR (`WindowsOcrReader`). Prices, all pages of current buy/sell transactions, and wallet coin balance come from the official Guild Wars 2 API.

Manage one or multiple GW2 API keys (`account`, `tradingpost`, `wallet`, `inventories` scopes) in **Settings → Manage API Keys** — keys are stored encrypted with **Windows DPAPI (`CurrentUser`)** in `%LOCALAPPDATA%\Mmopal\profiles\gw2-api-keys.dpapi` (automatically migrating legacy single-key `gw2-api-key.dpapi`), with status verification, custom account labels, copy/reorder/delete controls, and per-account SQLite isolation. You can also supply `MMOPAL_Native__Gw2ApiKey` as an environment variable override.

Template pictures load from `assets/templates/tradingpost` (configured by `Native:TemplateRoot` in `config/appsettings.json`) unless a `templateRoot` override is supplied in the payload. The worker never loads `core.dll` or `runtime/legacy`.

## Where files go

User data defaults to `%LOCALAPPDATA%\Mmopal`: logs, `mmopal.db`, checkpoints, profiles (`trading-limits.json`, `client-settings.json`, `operator-settings.json`, `custom-shoppinglist.json`, `blacklist.json`, `gw2-api-keys.dpapi`), and screenshots. Use **Open Data Folder** in **Settings** to inspect them. Do not commit API keys or runtime files.

## Manual check on the installed client

Do this by hand. There is no automated click-through.

1. Open **Settings**, paste and save your GW2 API key (DPAPI encrypted — auto-verified against `api.guildwars2.com`).
2. Click **Detect** (or enter your `Gw2-64.exe` path / `steam://rungameid/1284210`), choose your latency profile (default **High Ping** for 220–280ms VN→EU), set in-game **Resolution = Window**, **Interface Size = Larger**, and **DPI Scaling = Off**, then click **Launch & snap GW2** in **Session** to automatically resize and snap Guild Wars 2 to top-left `(0, 0)` at `1024×768`.
3. Log in and park characters next to Trading Post NPCs (`F` interact).
4. Test **Data source = Screen observe** in **Observe only** mode first to confirm TP detection and order book telemetry in the Session cards.
5. When ready for bounded autonomous trading, switch **Run mode** to **Live (Armed trading)**, check **Arm live input**, and click **Start session** (note button states dynamically lock and indicate `Running...` / `Paused`).
6. Stop anytime via **Stop**, **Emergency stop**, or `Ctrl+Shift+S` / `Ctrl+Shift+X`.
