# Run Mmopal

## First session (no game needed)

1. `dotnet run --project src/Mmopal.Desktop/Mmopal.Desktop.csproj`, or start `Mmopal.Desktop` in Rider.
2. **Guide & Setup** shows what is still missing.
3. On **Session**, keep **Dry run** and **Replay data** and click **Try offline sample**. The Activity Log shows what
   each cycle would do.

## Connect your account

Add a key in **Settings → Accounts & API** with the `account`, `tradingpost`, `wallet`, `inventories` and `characters`
permissions. Keys are encrypted with Windows DPAPI. Several accounts are fine; switch with **Active Account** in the
sidebar. Each has its own ledger, launcher platform (ArenaNet or Steam) and number of parked characters.

On every launch Mmopal syncs the account, runs Auto-Tune and scans the market (the **Startup** strip on Session).

## Going live

In the game:

- **Resolution = Window**, **Interface Size = Larger**, **DPI Scaling = Off**, Dynamic HUD = Always Hide All.
- Stand each character you rotate **right next to a Black Lion trader**, not the bank: the Delivery Box only opens
  there. Put them in the first Character Select slots.

In Mmopal:

1. **Settings → GW2 Client & Recovery:** **Detect** the game, choose the latency mode (High Ping for 200 ms+), and set
   **Parked chars** to how many characters you rotate.
2. **Session → Launch & snap GW2** (the monitor icon) puts the game at 1024×768 in the top-left corner.
3. Try **Observe only** first: the Game card should say **TP open**.
4. Switch to **Live (Armed trading)**. Screen observe and **Arm live input** are set for you. Press **Start session**. To have it start by itself, tick **Start at** and set a time (23:00 by default); Mmopal must be open, in Live with Arm live input on.

| Key | Button | Effect |
| --- | --- | --- |
| Ctrl+Shift+P | Pause / Resume | Starts nothing new until resumed. |
| Ctrl+Shift+S | Stop | Finishes the current step, saves the report (90 s at most). |
| Ctrl+Shift+X | Emergency stop | Stops at once, even mid-action. |

Leave the mouse and keyboard alone while it runs; its keys go to whatever has the focus.

## When something is off

| You see | Do |
| --- | --- |
| `Delivery Box still locked … on 'Name'` | Move that character next to a trader. Mmopal swaps after 30 minutes. |
| `buy orders paused` or `0 accepted` every cycle | Gold floor or the 50% buy-order cap reached. Wait for fills or change it in Settings → Trading & Portfolio. |
| `Not listing X: the bags hold N but only M …` | The stack holds copies of your own. Move them to the bank and the rest is listed. |
| `Order audit: … the API shows that order as …` | A wrong item was ordered. Cancel it in game. |
| `None of the search rows opened 'X'` | Blacklist X; the search cannot reach it. |
| `Auto-list cannot read your bags` | The key lacks `characters` or `inventories`. |

## Where files are

`%LOCALAPPDATA%\Mmopal` (**Settings → Open Data Folder**): `logs`, `mmopal.db` (ledger and reports), `profiles`
(settings, lists, encrypted keys), `screenshots` (frames of reads that failed). Never commit any of it.
