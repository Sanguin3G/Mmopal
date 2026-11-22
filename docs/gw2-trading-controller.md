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
2. Search the item name, then **wait until the result list shows it** (raw and contrast-enhanced OCR), or until "no items match this filter" appears. On high ping the previous search's rows linger, and clicking them hit a stale, similarly named item. A name whose misses clearly outnumber its confirmations (at least 2 misses and more than twice its confirmations; some names are rarely readable on screen) gets a short wait (a third of the UI wait: 2.5 s at High Ping) instead of the full 7.5 s; results that do appear show up in ~1.5 s, and the opened item's title is still checked. Before, one lucky read kept the full wait: 222 full waits (~28 min) in one 10 h session.
3. Open result row 1 and **read the dialog title**. If it is a different item, close it and try rows 2 and 3. An unreadable title proceeds and is logged. An item none of the rows opens twice (the game cuts long names such as "Soldier's Benthic Waterbreather of the Afflicted", so it cannot be told from similar items) is skipped for the rest of the session with a suggestion to blacklist it.
4. Set the price (Settings → GW2 Client & Recovery → **Price entry (buy & sell)**). **Type exact price** (default): before any click, read the live top bid from the Current Buyers list (row prices omit zero units, so "69. 95." is resolved against the API price), refuse if top bid + overbid is past the profitable ceiling, then type gold, silver and copper into the three boxes (located from the ▲ spinner). The Total must then be a whole multiple of the typed price; if it is not, the price is re-typed once, and a second mismatch aborts the order. Once `current/buys` lists the order the API price is compared with the typed one (`Order audit: …`). **Click top row** (older alternative): click Current Buyers row 1 and tap ▲ for the overbid; on high ping the click can land on a lower row, which step 5 catches. A settings file saved before typed entry became the default keeps its saved choice.
5. **Read the price by digit shape first** (`Worker/Vision/CoinPriceReader`): every GW2 number ends with its coin icon, so the reader finds bright blobs, tells coin discs from digit strokes, names each coin by colour (silver grey, gold yellow, copper orange), and matches digits against templates cut from real frames (`gw2-digit-glyphs.json`, embedded). It reads the top bid / lowest ask and the dialog Total exactly or returns nothing; it never guesses. When OCR confirms a total the shapes could not read (two OCR reads agreeing), the unknown digit shapes are learned into `%LOCALAPPDATA%\Mmopal\digit-glyphs.json`. If the shapes return nothing, OCR is used as before, calibrated on live frames: the **Total Price** line ÷ quantity first (plain text; gold is omitted when 0), then the Max. Price boxes (their ▴▾ spinners read as extra digits, and gold 0 reads as "o"). Abort when the price is >3% over or >10% under the planned price, but only on a Total-line reading; a boxes-only reading just logs. Unreadable dialogs (3 per session) are saved to `%LOCALAPPDATA%\Mmopal\screenshots\price-unreadable-*.png`; unconfirmed searches to `search-unconfirmed-*.png`.
6. Click Place Order.

Look for `Buy price check for X`, `Search row N for 'X' opened 'Y'` and `Search results for 'X' appeared after N ms` in the log.

## Outbid Refresh

Cancels outbid buy orders so the next cycle can re-bid, with three limits:

- only Mmopal's own buy orders are touched (same item, at a price one of its orders was placed at); orders you placed by hand stay;
- an order still at the **top bid after 48 hours** is cancelled too (it is not filling and its gold is better spent), and that item is not bought again for 12 hours; this does not count toward the bot-war pause;
- an order younger than **20 minutes** is kept (competing bots undercut by a few copper within minutes; cancelling on each undercut meant nothing filled);
- nothing is cancelled during the **00:00–01:30 UTC reset window**;
- an order is skipped when searching its name would list more than one of our buy orders, since the flow clicks the first Cancel button. This is logged once per item per session; cancel the extra order in game so Outbid Refresh can manage it.

Each cancel logs `our bid` vs `API top bid`.

## Other live behaviour

- **Message boxes:** OK dialogs such as "unable to gain access to the log-in server (Code=42:...)", common on Asia→EU routes, are dismissed in place and logged. Recovery takes over only if one keeps reappearing.
- **Delivery Box:** a collapsed panel (pushed down by a longer category list) is expanded by clicking its header. If the box says "Speak to a Black Lion Trading Company NPC" (TP opened away from a trader), nothing is clicked or credited; the TP is reopened through the trader at most every 10 minutes. It does not swap characters on every locked cycle (that caused ~12 logouts in 40 minutes and a server disconnect); only after 30 minutes locked, at most once an hour, it swaps early (`Swapping character early: the Delivery Box has been locked on 'Name' …`). The log names the character that is not at a trader when the name is readable. After a successful Take All, the same coins and item count are ignored for the ~5 minute API cache window.
- **Selling bought items** (Session → "Auto-list bought flip items from bags", remembered between launches): a bag item is listed when Mmopal placed a buy order for it in the last 30 days or it is on the current shopping list (synced history is not used: it includes personal purchases). **Your own copies are never sold:** Mmopal counts the units that are its own (buy fills at a price one of its orders was placed at, minus units sold since and units listed now; the synced history plus the newest history page, read every bag sync). The sell dialog lists a whole stack, so when a character holds more of an item than Mmopal's count (your food, infusions, a legendary that is also on the shopping list) that item is not listed (`Not listing X: the bags hold N but only M of them were bought by Mmopal`). Listings you cancel by hand go back into Mmopal's count, so it keeps trading its own units. It is held, not listed, when the lowest ask minus the undercut, after the 15% fees, would not cover the highest price Mmopal paid. Bag reads need an API key with the `characters` and `inventories` permissions; the log says so if they are missing. Listing uses the same price entry as buying. When the lowest ask is already one of Mmopal's own listings, a new unit is listed at that same price instead of undercutting it (on 22 Nov one item sat at five listings 2c apart). With **Type exact price**, the sell dialog's title is checked (bag rows 1–3 are tried if a row opens another item), the live lowest ask is read from Current Sellers before any click, a Current Sellers row is clicked only to switch the dialog from Sell Instantly to List This Item, and lowest ask − undercut is typed and verified against the Total (re-typed once). If that target is below break-even (the lowest price that still covers what Mmopal paid after the 5% + 10% fees), the item is held and checked again in 30 minutes. With **Click top row**, the older ▼ undercut taps are used. An item with an order's worth of unsold units in any character's bags is left out of the buy cycle until listed, and a character holding 10+ unsold flip items lists twice as many per sell pass. Bags belong to one character: an item missed twice waits until the next character swap or game restart instead of being searched again every 15 minutes. Each sell attempt logs `Sell pass Listed/Held/NotFoundInBags/SkippedError for X`; look for `Typed sell price for X` (a held item reads `holding: … below break-even`) and `Sell price check for X`. Collected coins are credited to the working balance until the wallet API catches up.
- **Launcher LOG IN:** at most 3 attempts per launcher pass, 6 s apart, each re-checked on a fresh frame so a click never lands on the news page. With the **Steam** platform LOG IN is never clicked (the Steam launcher signs in with the Steam account by itself); PLAY is clicked as soon as it appears, as on the ArenaNet launcher.
- **Character choice:** Character Select lists the last played character first. With more than one parked character, the bot never re-enters slot 1: on a fresh launch, a game restart, a disconnect, or an AFK swap it picks slot 2–min(N, 5) at random, or with "next" the last parked slot (the least recently played). The selected name is read before and after the click and logged.
- **First launch:** before the game is opened, the session reads every character's bags through the API (`Unsold flip items by character before launch: …`), so the first Character Select enters the parked character holding the most unsold stock (`entering parked slot #3 … (it holds the most unsold stock)`) instead of a rule-picked slot followed by a stock swap. A game restart or disconnect does the same, never re-entering the character the game just left.
- **Swapping to unsold stock:** buy orders and listings work anywhere, but a listing comes from the logged-in character's bags, and items bought while another character was logged in stay on that character. Each bag sync (API, every 2 minutes) counts the flip items each character holds. Every swap (AFK, Delivery Box locked, recovery) reads the names on the parked portraits and enters the parked character holding the most unsold items (`Character Select: 'Name' is in slot #3; swapping to it.`); when no name is read it falls back to the slot rule above. When another parked character holds at least 5 flip items, more than the current one, a swap is made just to list them, at most once an hour, or after 20 minutes once the current character has nothing left to list (`Swapping to Name to list its unsold flip items (…)`). Names come from the active API key's account, so each account uses its own characters. Only the first **Parked chars** portraits (5 at most) are ever entered, and that setting is remembered per account (a main can rotate 5 of 10 characters, an alt all 4). Character Select is only recognised by its Play button in the bottom-right corner: a whole-screen search once matched a red dialog button mid-screen, and clicking it logged the account out to the launcher on most swaps.
- **TP won't open on a character** (for example a bank NPC next to the trader keeps winning F): the first recovery step switches to another parked character, at most once every 20 minutes, before in-place retries and the game restart.
- **Disconnects:** a "lost its connection … wait a few minutes" message box starts a 5-minute wait before reconnecting. The game-restart allowance refills after 2 hours without a restart.
- **Long sessions:** Windows is kept awake (no display power-off, screensaver or sleep) while a live session runs. Sessions on the scanner's list re-run the Market Scanner every 3 hours by default (Market Scanner → More Filters → Auto re-scan) and swap in the fresh list, keeping the Custom Shopping List; a list already older than that is refreshed on the first cycle. The Income & Portfolio ledger syncs every 45 minutes (`Portfolio sync: N new trade(s) archived`). The Session page shows both: `Shopping list: 87 items (80 scanned) · re-scan in 1h 12m` and `History: 3 boosted · 2 skipped (lost money) · synced 21:40 UTC`.
- **Aborted buys:** an item whose buy was aborted for a wrong price or item waits 30 minutes before the next try.

## How the timings fit together

Three layers, each with one owner, so no rule fights another:

| Layer | Set by | What it decides |
| --- | --- | --- |
| **Latency mode** (Settings → GW2 Client & Recovery) | Low / Normal / High Ping / Custom | How long each UI step may wait: the UI wait (3 / 4.5 / 7.5 s), the pause after each click (350 / 550 / 900 ms) and the API timeout. Every driver wait derives from these: template waits use the UI wait, the search confirmation uses it (4 s at least), the short search wait for rarely readable names is a third of it (1.5–3 s), the logout and Character Select waits are multiples of it. |
| **Pace** (Session page: Safe / Balanced / Fast) | `config/profiles/*.json` | How much one pass does and how long to rest: up to 2 / 3 / 5 orders, listings and outbid cancels per pass, and a 90 / 50 / 25 s pause **between** cycles. A cycle itself takes about 1–3 minutes at high ping (median 110 s on 21-22 Nov), so the pause is added on top, not a cycle length. The pace tooltip shows the profile's real numbers together with the latency waits. |
| **Game and market rules** | fixed, with the measurement behind each in the code | Things that do not depend on ping: Delivery Box retry every 10 min and swap after 30 min locked (at most once an hour), recovery swaps at most every 20 min, server-disconnect cooldown 5 min, a stock swap at most once an hour, bag sync every 2 min, ledger sync every 45 min, market re-scan every 3 h, game-restart allowance refilled after 2 quiet hours. Launcher and loading-screen waits have floors (18–35 s) because loading takes that long at any ping. |

Changing the latency mode changes every UI wait at once; changing the pace never changes a UI wait.

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
