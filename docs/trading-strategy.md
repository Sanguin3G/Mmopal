# Trading strategy

How experienced Guild Wars 2 Trading Post flippers make money, what Mmopal does with that, and what is still on the roadmap. Written 2026-11-20 after the first long live sessions.

## What experienced traders agree on

- **Flip many items for small, reliable margins.** Profit comes from volume across many items, not from one big spread ([Ten Ton Hammer](https://www.tentonhammer.com/guides/guild-wars-2-how-to-make-money)). Spread capital over 10–20 items with stable spreads rather than one high-scoring item ([AuricDB](https://auricdb.com/guides/gw2-trading-post-flipping)).
- **Do the fee math every time.** A sale costs 15%: a 5% listing fee paid up front and non-refundable, plus a 10% exchange fee when it sells ([GW2 Wiki](https://wiki.guildwars2.com/wiki/TP)). A 30c nominal spread can leave about 10c after fees ([AuricDB](https://auricdb.com/guides/gw2-trading-post-flipping)).
- **Volume beats ROI.** The community tool [gnashblade](https://github.com/bexoo/gnashblade) ranks flips by expected daily profit: units traded per day (the smaller of units sold into buy orders and units bought from listings, from [DataWars2](https://api.datawars2.ie/gw2/v1/items/json)) × margin after the 15% fees. A wide spread on an item nobody trades is a trap: thin markets may not sell at the listed price ([GW2 forum](https://en-forum.guildwars2.com/discussion/comment/1261901)).
- **Don't fight 1c wars.** Check whether an item's book is full of 1-copper undercuts before committing ([Boostroom](https://boostroom.com/blog/trading-post-guide-flipping-tips-without-losing-gold)). Matching is reported to be first-in, first-out at a given price ([GW2 forum](https://en-forum.guildwars2.com/topic/7350-trading-post-queries/)), so constantly cancelling and re-posting loses your place.
- **Check in on a schedule, don't hover.** Review orders once or twice a day and drop items that don't move ([Boostroom](https://boostroom.com/blog/trading-post-guide-flipping-tips-without-losing-gold)).

## What our own data showed

On 2026-11-20 the shopping list (ranked by ROI and open-order depth) was full of 60–85% ROI items. DataWars2 trade counts for them over the previous day:

| Item | Bought via buy orders / day | Bought from listings / day |
| --- | --- | --- |
| Strong Ring | 0 | 3 |
| Cleric's Darksteel Imbued Inscription | 4 | 1 |
| Carrion Masquerade Boots | 4 | 2 |
| Slice of Allspice Cake | 1 | 0 |
| Glob of Ectoplasm (for comparison) | ~30,000 | ~470,000 |

Their top bids had not moved for hours, competitors undercut by 1–30c within minutes, and the GW2 API showed no filled buy orders for 12 days. High ROI with no volume is what the guides above warn against.

## What Mmopal does now

| Practice | In Mmopal |
| --- | --- |
| Rank by profit × volume | Market Scanner **Sort by = Est. profit/day** (default): net profit per unit × trades/day, capped at 20/day because a few small orders cannot use more volume. Trades/day is the DataWars2 7-day average of the slower side. |
| Skip items that don't trade | **Min trades/day** filter (default 10). The scan is volume-first: it downloads DataWars2's trade counts once (gzip, ~20 s, reused for 30 min), then prices **every** tradable item that trades often enough (about 1,600 items at 10/day), so nothing is missed by sampling. If DataWars2 is unreachable it falls back to the Scan scope sample and ranks by spread and depth. |
| Don't buy into spikes | **Skip price spikes** (on by default): an item whose top bid or lowest ask is more than 30% above its DataWars2 7-day average is skipped; the projected profit of a spiked price is not real. |
| Learn from your own trades | The last 30 days of your trades (Income & Portfolio) boost items you flipped fast at a profit and skip items that lost money on 2+ flips. The scanner's **Your history** column shows this, and Income & Portfolio lists the skipped items with an **Add to Blacklist** button. The ledger syncs at launch and every 45 minutes during a session. |
| Fee math | Every profit figure uses 5% listing + 10% exchange with the 1c minimums. |
| Don't chase undercuts | Outbid Refresh keeps an outbid buy order for at least 20 minutes and never cancels during the 00:00–01:30 UTC reset window. |
| Sell before buying again | With auto-list on, an item that still has an order's worth of unsold units in any character's bags is not bought again until they are listed (`Not buying N item(s) still unsold in bags until they are listed`; the Session page shows `N waiting to sell stock`). A character holding 10+ unsold items lists twice as many per sell pass, and swaps go to the parked character holding the most. On 21-22 Nov 97 bought items (~145g) sat unsold while the same items kept being re-bought. |
| Leave bot wars alone | An item outbid 3 times without a single fill is paused for 3 hours (`Pausing buys of X for 3 h`); a fill resets it. A buy aborted because the live top bid was already past our profitable price counts as an outbid too (Berserker's Ring: 10 such aborts in one night, never paused before). In one 7 h session ~100 buy orders went to items that never filled. |
| Keep gold free | At most 50% of total gold (wallet + buy orders + listings after fees) sits in buy orders (Settings → Trading & Portfolio); above that, new buys wait for fills. |
| Relist stale listings, carefully | A listing undercut for 12 h+ is cancelled and relisted when the item trades (it is on the scanner's list), it was not relisted in the last 24 h, and the new price still nets more than what was paid plus the 5% fee lost on the old listing. At most 2 per pass. Turn off in Settings → Trading & Portfolio. |
| Spread capital | Max buy price per unit and a gold reserve floor, quantity per order, 3 actions per cycle. Set by **Auto-Tune** (below). |
| Live re-pricing | Each cycle re-prices the list from `v2/commerce/prices` and drops items whose spread collapsed. |

## Auto-Tune

On by default (Settings → Trading & Portfolio → "Auto-Tune at every launch and Start session"). It runs when Mmopal opens and right before each session, never mid-session: the wallet barely changes within one session, and shifting buy limits under open orders would make the bot inconsistent.

1. **Size from total gold.** Wallet + gold in buy orders + listings after fees picks the tier (Starter < 100g, Balanced < 1,000g, High Capital) with ~4–5% of capital per unit and a 15% reserve.
2. **Learn from the last 14 days** (once at least 10 units have sold):
   - ROI under 5% after fees: Min ROI +3. ROI 15%+ with 70%+ winners: Min ROI −2 for more volume.
   - Under 50% winning flips: Min ROI +2 and busier items (Min trades/day ×1.5).
   - Items taking over 2 days to sell: Min trades/day ×2 and Max ROI capped at 60% (very wide spreads rarely fill).
3. **Where the gold sits:** half or more in unsold listings lowers Max Buy by 25% and asks for busier items until they sell; 70%+ idle in the wallet with profitable flips raises order size by 50%.

Min ROI stays within 8–25% and Min trades/day within 5–50. **Values you change by hand are kept** (for example a lower gold floor if you want to take more risk): the Settings page lists them. Clicking the **Auto-Tune** button (or **Let Auto-Tune decide**) hands them back; the automatic runs at launch and Start session keep them. The result and its reasons are in the Activity Log and the Auto-Tune button's tooltip.

## On launch

With "On launch: sync my GW2 account and scan the market" (on by default), Mmopal syncs Income & Portfolio, Auto-Tunes, then scans the market. The **Startup** strip on the Session page shows each step (○ waiting, … running, ✓ done, – skipped, ! failed). If the session uses the scanner's list and it is older than the Auto re-scan interval, the fresh scan replaces it; a hand-made list is never touched. Scanner filters are saved between launches.

## Using Lists & Blacklist

You don't need trading experience to use these. Let the scanner build the shopping list; use the blacklist for items you want the bot to leave alone.

- **Shopping list:** Market Scanner → preset → **Scan GW2 Market** → **Use All for Session**. After that the list refreshes itself: at launch when it is older than the Auto re-scan interval, and every 3 hours during a session (Market Scanner → More Filters → Auto re-scan). Your Custom Shopping List stays in the session through re-scans. The Session page shows `Shopping list: N items · re-scan in 1h 12m`.
- **Blacklist** (one name or `prefix *` per line):
  - items the log names in `Trading Post search for 'X' opens 'Y'` or `None of the search rows opened 'X'`: the bot cannot reach them reliably;
  - items sold under the same name in several rarities (some Masquerade armor, for example), since the item-name check cannot tell them apart;
  - anything you use yourself or are saving.

## Roadmap

Ordered by expected impact; none of these exist yet.

1. **Order size from volume:** quantity = min(configured quantity, ~5% of daily trades), so slow items get 1 unit and busy items more.
2. **Competition filter before buying:** gnashblade's listings-per-sale and delisted-per-sale from DataWars2 history, to skip bot-war items before the first order (today they are paused after 3 outbids).
3. **More auto-blacklist suggestions:** items with search mismatches, buys that stay unfilled for 24h+, or repeated spread collapse (items that lose money are already listed in Income & Portfolio).
