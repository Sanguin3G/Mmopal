# Trading strategy

What Mmopal trades, for how much, and why. Screen flows and log lines are in
[gw2-trading-controller.md](gw2-trading-controller.md).

## The idea

Many small, reliable flips beat a few big spreads. A sale costs **15%** (5% to list, not refunded, and 10% when it
sells), so volume and fee math matter more than ROI. On 20 Nov 2026 our ROI-ranked list was full of 60–85% items; their
real trades per day:

| Item | Bought by order | Bought from listings |
| --- | --- | --- |
| Strong Ring | 0 | 3 |
| Cleric's Darksteel Imbued Inscription | 4 | 1 |
| Slice of Allspice Cake | 1 | 0 |
| *Glob of Ectoplasm, to compare* | *~30,000* | *~470,000* |

No buy order had filled for 12 days. That is why everything below starts from volume.
Background: [GW2 Wiki](https://wiki.guildwars2.com/wiki/TP), [AuricDB](https://auricdb.com/guides/gw2-trading-post-flipping),
[gnashblade](https://github.com/bexoo/gnashblade), [Boostroom](https://boostroom.com/blog/trading-post-guide-flipping-tips-without-losing-gold).

## Rules

**Choosing items (Market Scanner)**

| Rule | Detail |
| --- | --- |
| Rank by profit × volume | Profit per unit after fees × trades/day (DataWars2 7-day average of the slower side, capped at 20). |
| Skip what does not trade | Min trades/day, default 10. Every item above it is priced, about 1,600. Without DataWars2, a sample ranked by spread and depth. |
| Skip spikes | Top bid or lowest ask more than 30% above its 7-day average. |
| Skip shared names | ~3,300 names belong to several items (Berserker's Ring is 11); a search cannot tell them apart. |
| Learn from you | Last 30 days of trades: fast profitable items are boosted, items that lost money on 2+ flips are skipped. |

**Buying**

| Rule | Detail |
| --- | --- |
| Live prices | The list is re-priced every cycle; collapsed spreads drop out. |
| Keep gold free | At most 50% of total gold (wallet + orders + listings after fees) in buy orders, above a reserve floor. |
| Sell before buying again | An item with an order's worth of unsold units in any bag is not bought. On 21–22 Nov 97 items (~145g) sat unsold while the same items were re-bought. |
| Do not chase undercuts | An outbid order stays at least 20 minutes. 00:00–01:30 UTC, when sellers dump, none is cancelled, unless the gold cap is reached: then those outbid for an hour are. |
| Drop what does not fill | A top bid unfilled after 24 hours is cancelled and the item waits 12 hours. 97% of fills come within 24 h. |
| Leave bot wars | Outbid 3 times without a fill: 3 hours' pause, then 6, 12 and 24. A fill resets it. Outbid 6+ times in the last day without a fill: paused for the first 6 hours. |
| Order size | The configured size, or 3% of a day's trades (10 at most). Doubled (20 at most) for proven items: 3+ orders in 14 days, half of them filled completely, 4+ units sold at a profit, half within 4 hours; not while two orders' worth is listed. An order never costs more than Max Buy; sizes on your own list stay. |
| Use idle gold | 5+ items waiting and a quarter of the gold idle: 5 per cycle and a 25 s pause until the backlog, the cap room or the gold is gone (not at Safe pace). Nothing to order for 5 cycles: items trading half as often are added, at most hourly. |

**Selling**

| Rule | Detail |
| --- | --- |
| Ask close to the API | A live lowest ask 6% above the API's, or 15% below, is not used; the API's is. |
| Never Sell Instantly | When a buyer offers the undercut price, the item is listed at the lowest ask itself. |
| No price, no listing | Without an API price to check against, an item is held. |
| Never below cost | Held until the price covers the highest price paid plus fees. |
| Do not undercut yourself | When the lowest listing is ours, a new unit joins it at the same price. |
| Relist rarely | Only when undercut for 12 h+, the item still trades (after 24 h, any item), not relisted in 24 h, and the new price still covers cost plus the lost 5%. With several listings, all must qualify. At most 2 per pass. |

## Auto-Tune

Runs at launch and before each session, never during one (Settings → Trading & Portfolio).

1. **Size from total gold:** Starter (< 100g), Balanced (< 3,000g) or High Capital; ~4–5% of capital per unit, 8%
   reserve (Starter 15%). High Capital drops everything under 1g: at 1,158g that was half of the profit.
2. **Last 14 days** (10+ units sold):

   | Result | Change |
   | --- | --- |
   | ROI under 5% | Min ROI +3 |
   | ROI 15%+ and 70%+ winners | Min ROI −2 |
   | Under 50% winners | Min ROI +2, Min trades/day ×1.5 |
   | Items take 2+ days to sell | Min trades/day ×2, Max ROI 60% |

3. **Where the gold sits:** half or more in listings lowers Max Buy by 25%; 70%+ idle with profitable flips raises order
   size by 50%.

Min ROI stays within 8–25%, Min trades/day within 5–50. Values you set by hand are kept until you click **Auto-Tune**.

## Lists

- **Shopping list:** the scanner fills it and refreshes it every 3 hours and at launch when older. Your Custom Shopping
  List always stays in.
- **Blacklist** (a name or `prefix *`): items the log says cannot be reached, and anything you use or save yourself.

## Not built yet

1. Skipping bot-war items before the first order, from DataWars2 listing history.
2. Session hours set from your own best hours (Income → Best hours shows them).
