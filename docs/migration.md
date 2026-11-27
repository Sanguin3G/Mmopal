# Migration from the original Mmopal

The original Mmopal (compared here: 1.9.2, January 2026) was an abandoned multi-game bot: a .NET 6 window running Lua
scripts through a closed native module. This project replaces its Guild Wars 2 Trading Post part with a .NET 10
application. Nothing of the original is loaded or linked any more.

## Before and after

| | Original | Now |
| --- | --- | --- |
| Runtime | .NET 6 window + Lua + `core.dll` (8 MB, packed, no source) | .NET 10: WPF window + worker process |
| Games | GW2, Albion, OSRS, RS3, FFXIV | GW2 Trading Post |
| GW2 trading code | 2,906 lines of Lua in two files | Six C# projects, split by concern |
| Vision | `core.dll` over OpenCV 4 and Tesseract | Own capture, OpenCvSharp, digit shapes, Windows OCR |
| Native DLLs shipped | 28 | OpenCV only (SQLite and OCR come with Windows) |
| Automated tests | None | 489, without the game and without input |
| API key | Plain text in `settings.ini`, first 20 characters logged | Windows DPAPI, never logged |
| State | CSV files | SQLite: ledger, reports, metrics |
| Failure handling | Kill the game, restart the script | Recovery steps, then a bounded game restart |

## What changed in the trading flow

| Step | Original (`tradingpost.lua`) | Now |
| --- | --- | --- |
| Items | Scraped gw2bltc / gw2profits pages | Official API prices + DataWars2 volume |
| Item check | Clicks the first result | Reads the window title, word by word |
| Price | Ctrl+A, Ctrl+C of each box; in API mode not read at all | Digit shapes, checked against the API |
| Order check | None (API mode: profit, price and quantity checks are `TODO`) | Total must equal price × quantity |
| Field entry | Ctrl+A | End, Backspace, Delete (Ctrl+A moves the character) |
| Selling | Lists everything in the bags, then clicks Sell Instantly if the dialog is still open | Only units Mmopal bought, never instantly, never below cost |
| Cancelling | First Cancel in the list | Only when the list shows the item, and only Mmopal's own orders |
| Rhythm | Fixed buy and relist phases (minutes) | One cycle, paced by profile; waits follow the ping |

## How it was moved

| Phase | When (2026) | Work |
| --- | --- | --- |
| Foundation | 11 Aug | Contracts, trading rules, worker process and tests; the Lua app kept as a reference |
| Inspection | by 20 Nov | `core.dll` exports, imports and Lua table listed to learn which calls had to be replaced. Its code was not recovered or copied |
| Replacement | by 20 Nov | Capture, template match, OCR, input and API client in C#; live buying, selling, cancelling, login, swaps |
| Live hardening | 20–23 Nov | Long sessions on a 220–280 ms route; rules added from failures measured in the logs |
| Removal | 21 Nov | `runtime/legacy` and `archive` deleted (121 files); no bridge left |
| Restructuring | 21 Nov | Window split into pages, driver and runtime into partial files |

Each `core.dll` call and what does its job now:

| Original call | Replacement |
| --- | --- |
| `Image`, `GetCoordinates` | `FrameTemplateMatcher` |
| `GetPixels` | `FrameVision.CountPixels` |
| `PerformOCRAndParse`, `DetectNumbers` | `CoinPriceReader`, `WindowsOcrReader` |
| `ClickAtPosition`, `KeyPress`, `SendString` | `Win32InputController` |
| `GW2_GetCurrentTransactions`, `GW2_GetGoldBalance`, `GW2_GetListingPrices` | `Gw2OfficialDataProvider` |
| `GW2_Scraper_BLTC`, `GW2_Scraper_GW2Profits` | `Gw2MarketScanner` (CSV import kept) |
| `OpenProgram`, `PositionProcessWindowToTopLeft`, `ResizeProcessWindow` | `Gw2ClientPreparer`, `Win32WindowController` |
| `Sleep`, `Log`, `StopRequested` | Cancellable waits, structured log, worker commands |

## Problems worth knowing

| Problem | Seen | Solution |
| --- | --- | --- |
| Coin prices misread by OCR | Gold 0 read as "o", spinner arrows read as digits | Digits matched by shape, coins by colour; exact or nothing |
| Wrong item ordered | Infusion (Red) at 95g for (Purple) at 21g, 22 Nov | Word-by-word title check; an unread Total aborts |
| Orders not recognised as ours | Price match missed 26 of 707 fills | Match by creation time: 707 of 707 |
| Own items nearly listed | Personal copies share the item name | Count units from own fills; leave mixed stacks |
| Characters drifting from the trader | Ctrl+A strafes when no text box has the focus | No letter chords |
| API up to 5 minutes behind | Double orders, "locked" boxes that were empty | Per-item cooldown; the screen decides |
| High ping | Clicks on the previous search's rows | Wait until the row shows the name |

## What is left of the original

| Kept | Where | Note |
| --- | --- | --- |
| 63 template images | `assets/templates/tradingpost` | Unchanged; byte-identical to 1.9.2 |
| Name, 1024×768 window, key flow | throughout | F opens the trader, rarity and search filters |
| GW2BLTC / GW2Profits CSV import | Market Scanner | Optional |
| Fishing activity | `StateMachines.cs`, Session page | Placeholder, sends no input |
| `IArduinoInput`, `DryRunInputController` | `NativeAbstractions.cs` | Unused |
| Licensing module | `Licensing.cs` | Disabled |

Left out on purpose: `GW2_FishHack` (changes the running game), the other games, and Arduino input.

The copy examined holds no licence file, so the images should be replaced with own captures before the repository is made
public.
