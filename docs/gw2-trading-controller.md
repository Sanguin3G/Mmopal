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
2. Search the item name, then **wait until the result list shows it** (raw and contrast-enhanced OCR), or until "no items match this filter" appears. On high ping the previous search's rows linger, and clicking them hit a stale, similarly named item.
3. Open result row 1 and **read the dialog title**. If it is a different item, close it and try rows 2 and 3. An unreadable title proceeds and is logged.
4. Click Current Buyers row 1 (sets the price to the top bid), set quantity, tap ▲ for the overbid.
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
- **Delivery Box:** a collapsed panel (pushed down by a longer category list) is expanded by clicking its header. If the box says "Speak to a Black Lion Trading Company NPC" (TP opened away from a trader), nothing is clicked or credited: the TP is reopened through the trader, and if a later cycle is still locked the bot switches parked character. After a successful Take All, the same coins and item count are ignored for the ~5 minute API cache window.
- **Selling bought items** (Session → "Auto-list bought flip items from bags", remembered between launches): a bag item is listed when it is something Mmopal placed a buy order for in the last 30 days, is on the current shopping list, or is a synced past purchase. Bag reads need an API key with the `characters` and `inventories` permissions; the log says so if they are missing. Each sell attempt logs `Sell pass Listed/NotFoundInBags/SkippedError for X`. Collected coins are credited to the working balance until the wallet API catches up.
- **Launcher LOG IN:** at most 3 attempts per launcher pass, 6 s apart, each re-checked on a fresh frame so a click never lands on the news page. With the **Steam** platform nothing is clicked on the launcher: Steam signs in by itself, and PLAY is clicked only if the game has not started after 20 s.
- **Character choice:** Character Select lists the last played character first. With more than one parked character, the bot never re-enters slot 1: on a fresh launch, a game restart, a disconnect, or an AFK swap it picks slot 2–min(N, 5) at random, or with "next" the last parked slot (the least recently played). The selected name is read before and after the click and logged.
- **TP won't open on a character** (for example a bank NPC next to the trader keeps winning F): the first recovery step switches to another parked character before in-place retries and the game restart.

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
