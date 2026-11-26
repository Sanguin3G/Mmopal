# Live flows

How a cycle works on screen, and the log lines to look for. Why a rule exists is in
[trading-strategy.md](trading-strategy.md); measurements behind limits are next to them in the code.

## The cycle

```text
TP open check → Delivery Box → re-price list → plan and place buy orders
→ list bought items → Outbid Refresh → relist → character swap when due → pause
```

## Planning

Blacklisted, already ordered, unpriced and unprofitable items are skipped. The rest are walked in rank order:

1. **Wrong-item guard:** an item whose order showed up in the API under another name is skipped for the run
   (`Order audit: …`).
2. **Cooldown:** 6 minutes per item, longer than the API's ~5 minute lag.
3. **Cap per cycle**, from the pace.
4. **Gold:** wallet minus the reserve floor, and the buy-order share.

`GW2 cycle plan: N accepted, M skipped (…)` sums it up. A failed action is never blindly repeated: the next cycle
reads the orders first.

## Buying

| Step | Check |
| --- | --- |
| Open the TP | **F** (trader), then **O**, then **F**; Esc in between closes a bank opened by mistake. |
| Search | Only with the Trading Post proven on screen (Black Lion crest and transactions tab). Enter and Backspace are pressed only once the box shows the typed name. Then waits until the results show it. Rarely readable names get a third of the UI wait. |
| Open a row | The title must be the item (rows 1–3 are tried). A title on two lines needs its second line too, and every read follows the window, which sits lower then. Unreadable: only goes on if the search showed the name and the live price is within 10% of the API's. |
| Read the price | The top row only, once the window stands still: by digit shape (exact or nothing), else OCR. A read with fewer numbers than the API price, or 15% below it, is not used. Clearly over double or under half the API price, the window is another item's: aborted. Top bid + overbid past the profitable price aborts. |
| Type price and quantity | The Total must be the typed price × at most the ordered quantity. Re-typed once, then aborted. |
| Place Order | Later the API's order is compared with what was typed (`Order audit: …`). |

An item aborted for a wrong price or title waits 30 minutes. An item no row opens twice is skipped for the session.

Keys reach the game whenever no text box has the focus, even with the TP open: Ctrl+A moves the character, Backspace
replies to a whisper, Enter opens chat. So nothing is typed at a guessed position, the search box is cleared by selecting
its text with the mouse, and price boxes are typed only while their dialog is on screen. If the TP closed, it is opened
again before selling or cancelling (`The Trading Post is not on screen before the …`).

**Click top row** (Settings → GW2 Client & Recovery → Price entry) is the older alternative to typing.

## Selling

- **What:** items Mmopal ordered in the last 30 days or on the shopping list, found in the logged-in character's bags.
- **Only its own units:** an order is Mmopal's when GW2 created it while Mmopal was placing one for that item. Units =
  those fills − sold − listed. The sell dialog lists a whole stack, so a stack with more than that is left alone
  (`Not listing X: the bags hold N but only M …`). Listings you cancel by hand come back into the count.
- **Price:** lowest ask − undercut, typed and checked like a buy. Below break-even it is held and tried again in 30
  minutes.
- **Queue:** a listing that fails is retried after 2, 4, 8, 16, then every 30 minutes. An item missed twice waits for the next character. One that no bag has shown for 10 minutes is dropped.

`Sell pass Listed / Held / NotFoundInBags / SkippedError for X` is logged for each item.

## Cancelling

**Outbid Refresh** cancels orders that are outbid (20+ minutes old) or unfilled for 24 hours, so the next cycle can bid
again. It clicks the first Cancel its search shows, so an order is skipped when that search would also list one that
stays: yours, a young one, or one at the top bid. A blurred first row counts only if it is clearly closer to the name than
to any other order of yours. A row whose name cannot be read is cancelled only when it is the one row shown, at a price no other order of yours has. An item it cannot find on screen twice is left for an hour, then 2, 4, 8 and 16. An order for
another item, placed at that second and price, is Mmopal's mistake and is cancelled too.

**Relist** does the same for listings, not in the last 10 minutes of a timed session, and never for an item you listed copies of yourself.

## Popups over the item window

The green **Success** box ("Pick up your coin at the nearest Black Lion trader") is closed with its OK button before each price
box is clicked and again before its keys are pressed (`Closed a 'Success' box …`); if it stays, nothing is typed.

## Reading names

The font of the lists and the search box is Trebuchet MS, which comes with Windows. Mmopal draws the expected name and
compares it with the screen letter by letter (`DrawnNameReader`): it has to fit as a whole, in its worst letter, in every digit
against the other nine, and nothing may follow it. On 45 rows OCR could not read (20–24 Nov) it finds all 45; of 83,328 other
names of GW2 items none passed. Windows OCR is the fallback, and reads the large title of the item window.

A row still not found is kept as `screenshots\names\<item>.png`. Moved to `tests/…/Fixtures/name-rows` it is a test. The session
report says how well the screen was read (`Read on screen: name 99.0% of 1000, …`).

## Frozen Trading Post

Three things in a row that do nothing on screen (an item window that does not open, a search neither the box nor the list
takes, a Take All that empties nothing) mean it is frozen: the rest of the pass is skipped, a `tp-frozen-*` frame is saved, and
the next cycle closes and reopens it. Frozen again within 15 minutes, or not closing, it swaps character, else recovers
as after a failed cycle.

## Delivery Box and characters

- **Collected** means the box changed after Take All.
- **Locked box** ("Speak to a Black Lion Trading Company NPC" and still so after Take All): the TP is reopened at the
  trader at most every 10 minutes; after 30 minutes locked, at most once an hour, Mmopal swaps character. The same words
  show at a trader when the box holds only coins, and an empty box ("When you buy or sell …") is not locked unless the API
  still disagrees after 10 minutes.
- **Which character:** the parked one holding the most of Mmopal's unsold items, by name (the bags are read while the
  launcher logs in); otherwise slot 2–N at random.
  Slot 1 (last played) is never re-entered. Only the first **Parked chars** slots are used.
- **Swap for stock:** to a character with 5+ unsold items, more than the current one, once an hour, or after 20 minutes
  once nothing is left to list here. With 10+ items a character lists twice as many per pass.
- **Unread name:** when Character Select cannot be read, two listed items that only one character held tell which one it
  is (`Logged-in character is …`).
- **Logging out:** Log Out, then **Character Select** in "Are you sure you want to log out?", found by its text. Closing the game
  at the session cap logs out the same way first and says so if Character Select did not appear.
- **Character Select:** nothing is clicked until the selected name reads the same twice, since it takes clicks late while
  it loads. If it closes by itself, no further portrait is clicked. Play is clicked. Enter is not pressed: it landed in the loaded world and opened chat.

## Launch and recovery

- **LOG IN:** 3 attempts, 12 s apart (a login takes about 6.6 s), each tap only while LOG IN is on screen. Never clicked on Steam, which signs in by itself.
- **Message boxes** such as log-in server Code=42 are dismissed. A lost connection waits 5 minutes first.
- **TP will not open:** another character (every 20 minutes at most), then retries, then a game restart. The restart
  allowance refills after 2 quiet hours.
- **Long sessions:** Windows stays awake; the market is re-scanned every 3 hours and the ledger synced every 45 minutes.

## Timing

| Layer | Set by | Decides |
| --- | --- | --- |
| Latency mode | Settings → GW2 Client & Recovery | UI wait 3 / 4.5 / 7.5 s, pause after a click 350 / 550 / 900 ms, API timeout. Every driver wait derives from these. |
| Pace | Session | 2 / 3 / 5 orders, listings and cancels per pass; 90 / 50 / 25 s between cycles. Balanced goes to 5 and 25 s by itself while items wait and gold is idle (`Faster pace: …`). A cycle itself takes 1–3 minutes at high ping. |
| Game rules | Fixed in code | Everything above that does not depend on ping. |

## Evidence

**Reports → Findings** says what stood out in a session: stuck gold, wasted searches, repeated aborts, where the time went.
Behind it, `mmopal.db` keeps every live session for 180 days:

| Table | One row per | Holds |
| --- | --- | --- |
| `cycle_metrics` | cycle | JSON: character, market phase, seconds per step, accepted and skip reasons, wallet, gold in orders (and over 12 h old), outbid and undercut counts, unsold units |
| `session_events` | buy, sell, cancel, relist, pickup, swap, pace change, frozen, search, reading | outcome code (`placed`, `listed`, `held`, `not-found`, `title-unreadable`, `unconfirmed`, …), item, character, price |

```sql
-- what keeps failing, over all runs
SELECT kind, outcome, COUNT(*) FROM session_events GROUP BY 1, 2 ORDER BY 3 DESC;
-- seconds per cycle spent buying, by market phase
SELECT json_extract(payload, '$.MarketPhase'), AVG(json_extract(payload, '$.Seconds.buy')) FROM cycle_metrics GROUP BY 1;
```

**Income** counts only Mmopal's own trades for *Bought, not sold yet* and **Best hours**, which divides fills by the time Mmopal ran in each hour; hours under 2 h of running time are muted.

Logs: `%LOCALAPPDATA%\Mmopal\logs\mmopal-YYYYMMDD.jsonl` (UTC). Unreadable dialogs: `screenshots\price-unreadable-*`,
`total-unreadable-*`, `total-mismatch-*`, `wrong-item-price-*`, `character-select-unread-*` (3 of a kind an hour); no List button: `list-button-missing-*`; unconfirmed searches: `search-unconfirmed-*`. Learned digit shapes: `digit-glyphs.json`.
