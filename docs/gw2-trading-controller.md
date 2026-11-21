# GW2 trading controller

One trading cycle in the worker:

```text
TP open check (dismiss message boxes) -> Delivery Box -> re-price shopping list from v2/commerce/prices
-> TradingCycleController: market snapshot -> planner -> select actions -> execute (Dry run / Observe / Live)
-> inventory sell pass -> Outbid Refresh -> AFK character swap when due -> wait
```

`TradingCycleController` (`src/Mmopal.Gw2/TradingController.cs`) depends on `IGw2MarketReader` and `ITradingActionExecutor`, so the same decisions run against replay data, Observe, Dry run and Live.

## Choosing actions

The planner skips blacklisted items, items with an open buy order, incomplete prices and unprofitable spreads (5% listing + 10% exchange, 1c minimums). The controller then walks the ranked decisions once, in order:

1. **Wrong-item guard.** If an item bought this run never appeared under its own name in `current/buys`, while an order for a longer name containing it did (for example "Slice of Allspice Cake" → "Slice of Allspice Cake with Ice Cream"), the item is skipped for the rest of the run.
2. **Recent-order cooldown.** An item ordered in the last 6 minutes is skipped. This outlasts the GW2 API's ~5 minute `current/buys` cache. It is keyed by item, not price, so a 1c move in the quote cannot place a second order.
3. **Per-cycle cap** (`MaxActionsPerCycle`, from the pace profile).
4. **Gold budget**: wallet minus the reserve floor, charged only for actions that will run.

Buys pause entirely at 0g, at or below the reserve floor, or when the balance is unknown. The log shows accepted decisions plus one `GW2 cycle plan: N accepted, M skipped (...)` summary line; individual skips are at Debug level.

Action execution is not blindly retried: an executor exception moves the cycle to `Recovering`, and the next cycle reconciles orders from the API first.

## Live buy flow

`Gw2TradingPostUiDriver.PlaceBuyOrderAsync`, calibrated on 1024×768 screenshots:

1. Make sure the TP is open; click OK on any in-game message box first. Opening tries **F** (the nearest NPC), then **O** (the Black Lion panel works anywhere; its sidebar scales icon at about (33, 224) switches Gem Store → Trading Post), then **F** again, pressing Esc in between so a bank or other NPC window opened by mistake is closed.
2. Search the item name, then **wait until the result list shows it** (raw and contrast-enhanced OCR), or until "no items match this filter" appears. On high ping the previous search's rows linger, and clicking them hit a stale, similarly named item. A name that has never been confirmed after two searches (some never read on screen) gets a 2 s wait instead of 7.5 s; the opened item's title is still checked.
3. Open result row 1 and **read the dialog title**. If it is a different item, close it and try rows 2 and 3. An unreadable title proceeds and is logged.
4. Set the price (Settings → GW2 Client & Recovery → **Price entry (buy & sell)**). **Type exact price** (default): before any click, read the live top bid from the Current Buyers list (row prices omit zero units, so "69. 95." is resolved against the API price), refuse if top bid + overbid is past the profitable ceiling, then type gold, silver and copper into the three boxes (located from the ▲ spinner). The Total must then be a whole multiple of the typed price; if it is not, the price is re-typed once, and a second mismatch aborts the order. Once `current/buys` lists the order the API price is compared with the typed one (`Order audit: …`). **Click top row** (older alternative): click Current Buyers row 1 and tap ▲ for the overbid; on high ping the click can land on a lower row, which step 5 catches. A settings file saved before typed entry became the default keeps its saved choice.
5. **Read the price**, calibrated on live frames: the **Total Price** line ÷ quantity first (plain text; gold is omitted when 0), then the Max. Price boxes (their ▴▾ spinners read as extra digits, and gold 0 reads as "o"). Abort when the price is >3% over or >10% under the planned price, but only on a Total-line reading; a boxes-only reading just logs. Unreadable dialogs (3 per session) are saved to `%LOCALAPPDATA%\Mmopal\screenshots\price-unreadable-*.png`; unconfirmed searches to `search-unconfirmed-*.png`.
6. Click Place Order.

Look for `Buy price check for X`, `Search row N for 'X' opened 'Y'` and `Search results for 'X' appeared after N ms` in the log.

## Outbid Refresh

Cancels outbid buy orders so the next cycle can re-bid, with three limits:

- an order younger than **20 minutes** is kept (competing bots undercut by a few copper within minutes; cancelling on each undercut meant nothing filled);
- nothing is cancelled during the **00:00–01:30 UTC reset window**;
- an order is skipped when searching its name would list more than one of our buy orders, since the flow clicks the first Cancel button.

Each cancel logs `our bid` vs `API top bid`.

## Other live behaviour

- **Message boxes:** OK dialogs such as "unable to gain access to the log-in server (Code=42:...)", common on Asia→EU routes, are dismissed in place and logged. Recovery takes over only if one keeps reappearing.
- **Delivery Box:** a collapsed panel (pushed down by a longer category list) is expanded by clicking its header. If the box says "Speak to a Black Lion Trading Company NPC" (TP opened away from a trader), nothing is clicked or credited; the TP is reopened through the trader at most every 10 minutes. It does not swap characters on every locked cycle (that caused ~12 logouts in 40 minutes and a server disconnect); only after 30 minutes locked, at most once an hour, it swaps early (`Swapping character early: the Delivery Box has been locked on 'Name' …`). The log names the character that is not at a trader when the name is readable. After a successful Take All, the same coins and item count are ignored for the ~5 minute API cache window.
- **Selling bought items** (Session → "Auto-list bought flip items from bags", remembered between launches): a bag item is listed when Mmopal placed a buy order for it in the last 30 days or it is on the current shopping list (synced history is not used: it includes personal purchases). It is held, not listed, when the lowest ask minus the undercut, after the 15% fees, would not cover the highest price Mmopal paid. Bag reads need an API key with the `characters` and `inventories` permissions; the log says so if they are missing. Listing uses the same price entry as buying. With **Type exact price**, the sell dialog's title is checked (bag rows 1–3 are tried if a row opens another item), the live lowest ask is read from Current Sellers before any click, a Current Sellers row is clicked only to switch the dialog from Sell Instantly to List This Item, and lowest ask − undercut is typed and verified against the Total (re-typed once). If that target is below break-even (the lowest price that still covers what Mmopal paid after the 5% + 10% fees), the item is held and checked again in 30 minutes. With **Click top row**, the older ▼ undercut taps are used. Bags belong to one character: an item missed twice waits until the next character swap or game restart instead of being searched again every 15 minutes. Each sell attempt logs `Sell pass Listed/Held/NotFoundInBags/SkippedError for X`; look for `Typed sell price for X` (a held item reads `holding: … below break-even`) and `Sell price check for X`. Collected coins are credited to the working balance until the wallet API catches up.
- **Launcher LOG IN:** at most 3 attempts per launcher pass, 6 s apart, each re-checked on a fresh frame so a click never lands on the news page. With the **Steam** platform LOG IN is never clicked (the Steam launcher signs in with the Steam account by itself); PLAY is clicked as soon as it appears, as on the ArenaNet launcher.
- **Character choice:** Character Select lists the last played character first. With more than one parked character, the bot never re-enters slot 1: on a fresh launch, a game restart, a disconnect, or an AFK swap it picks slot 2–min(N, 5) at random, or with "next" the last parked slot (the least recently played). The selected name is read before and after the click and logged.
- **TP won't open on a character** (for example a bank NPC next to the trader keeps winning F): the first recovery step switches to another parked character, at most once every 20 minutes, before in-place retries and the game restart.
- **Disconnects:** a "lost its connection … wait a few minutes" message box starts a 5-minute wait before reconnecting. The game-restart allowance refills after 2 hours without a restart.
- **Long sessions:** Windows is kept awake (no display power-off, screensaver or sleep) while a live session runs. Sessions on the scanner's list re-run the Market Scanner every 3 hours by default (Market Scanner → More Filters → Auto re-scan) and swap in the fresh list, keeping the Custom Shopping List; a list already older than that is refreshed on the first cycle. The Income & Portfolio ledger syncs every 45 minutes (`Portfolio sync: N new trade(s) archived`). The Session page shows both: `Shopping list: 87 items (80 scanned) · re-scan in 1h 12m` and `History: 3 boosted · 2 skipped (lost money) · synced 21:40 UTC`.
- **Aborted buys:** an item whose buy was aborted for a wrong price or item waits 30 minutes before the next try.

## Replay command payload

The worker `start` command may carry a JSON payload:

```json
{
  "candidates": [
    { "itemName": "Replay Profit", "quantity": 2, "buyPriceCopper": 10000, "sellPriceCopper": 20000 }
  ],
  "candidatesPath": null,
  "blacklist": ["Unwanted *"],
  "blacklistPath": null,
  "transactions": [],
  "balance": { "copper": 1000000 },
  "policy": { "minimumProfitPercentage": 50 }
}
```

Dry run records simulated actions in SQLite. Observe reports the same decisions without input.
