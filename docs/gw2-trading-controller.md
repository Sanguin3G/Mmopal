# GW2 trading controller

The trading slice is a deterministic controller for DryRun and Observe modes. It processes each cycle as:

```text
market snapshot -> transaction index -> blacklist checks
-> price/fee (1c minimum)/profit checks -> bounded action plan -> checkpoint
```

The controller never calls Lua or native code directly. It depends on `IGw2MarketReader` and `ITradingActionExecutor`, so the same decisions can be tested against replay data before a live executor exists.

## Replay command payload

The worker `start` command may carry a JSON string in `payload`:

```json
{
  "candidates": [
    {
      "itemName": "Replay Profit",
      "quantity": 2,
      "buyPriceCopper": 10000,
      "sellPriceCopper": 20000
    }
  ],
  "candidatesPath": null,
  "blacklist": ["Unwanted *"],
  "blacklistPath": null,
  "transactions": [],
  "balance": { "copper": 1000000 },
  "policy": { "minimumProfitPercentage": 50 }
}
```

DryRun records simulated actions in SQLite and sends decision counts through the worker health event. Observe reports the same decisions without recording input as an executable action. Action deduplication is scoped per `runId` (combining `runId` and the action signature), so repeated cycles within an active session do not repeat the same action, while earlier DryRun or Observe runs never permanently block future sessions.

Market reads use bounded exponential retries. Action execution is **not** blindly retried on failure: an execution error moves the cycle to `Recovering` so the next cycle reconciles open orders from the API first. If checkpoint persistence fails after an action already executed, the controller keeps an in-memory guard for that action rather than falsely marking it `Failed`.

Listing (5%) and exchange (10%) fees enforce Guild Wars 2's 1-copper minimum whenever price and fee rate are positive.

An absent balance, stale snapshot, invalid price, existing buy order, blacklisted item, or exhausted market-read retry budget results in a safe non-action or recovery state.

Live mode does not send clicks, keys, or orders. A live action executor waits until that path is deliberately armed.

Each trading health event also describes current orders against the public book. A buy is behind when the highest bid is above our price. A sell is behind when the lowest ask is below our price. A missing or non-positive quote stays unknown. That sentence does not place, cancel, or reprice an order.
