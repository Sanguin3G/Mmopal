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
| Skip items that don't trade | **Min trades/day** filter (default 10). If DataWars2 is unreachable the scan still runs and ranks by spread and depth. |
| Fee math | Every profit figure uses 5% listing + 10% exchange with the 1c minimums. |
| Don't chase undercuts | Outbid Refresh keeps an outbid buy order for at least 20 minutes and never cancels during the 00:00–01:30 UTC reset window. |
| Spread capital | Max buy price per unit and a gold reserve floor (Auto-Tune: ~4% of wallet per unit, 15% reserve), quantity per order, 3 actions per cycle. |
| Live re-pricing | Each cycle re-prices the list from `v2/commerce/prices` and drops items whose spread collapsed. |

## Using Lists & Blacklist

You don't need trading experience to use these. Let the scanner build the shopping list; use the blacklist for items you want the bot to leave alone.

- **Shopping list:** Market Scanner → preset → **Scan GW2 Market** → **Use All for Session**. Re-scan at least once a day; the bot does not re-scan by itself yet.
- **Blacklist** (one name or `prefix *` per line):
  - items the log names in `Trading Post search for 'X' opens 'Y'` or `None of the search rows opened 'X'`: the bot cannot reach them reliably;
  - items sold under the same name in several rarities (some Masquerade armor, for example), since the item-name check cannot tell them apart;
  - anything you use yourself or are saving.

## Roadmap

Ordered by expected impact; none of these exist yet.

1. **Auto re-scan during long sessions** (for example every 6 hours, or when fewer than N listed items remain profitable), so the list follows the market.
2. **Order size from volume:** quantity = min(configured quantity, ~5% of daily trades), so slow items get 1 unit and busy items more.
3. **Competition filter:** gnashblade also defines listings-per-sale, delisted-per-sale and day-over-day spread compression from DataWars2 history; use them to skip bot-war items.
4. **Auto-blacklist suggestions:** surface items with search mismatches, buys that stay unfilled for 24h+, or repeated spread collapse, with a one-click "Blacklist" in the Activity Log.
5. **Sell-side rules:** a minimum time before relisting an undercut sell, since each relist costs another 5% listing fee.
