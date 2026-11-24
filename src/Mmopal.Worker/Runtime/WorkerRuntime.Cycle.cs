using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mmopal.Contracts;
using Mmopal.Gw2;
using Mmopal.Native;

namespace Mmopal.Worker;

/// <summary>
/// The trading session loop. Each cycle runs these steps in order:
/// housekeeping (restart allowance, stop, pause, session cap) → Trading Post open and Delivery Box →
/// trade history and bags → shopping list re-scan → decision cycle → sell pass → outbid refresh → relist →
/// character swap → status → failed-cycle recovery → wait. Pause is checked between steps, so nothing new
/// starts after the operator pauses.
/// </summary>
public sealed partial class WorkerRuntime
{
    /// <summary>What the loop does after a step: go on with this cycle, start the next one, or end the session.</summary>
    private enum CycleStep
    {
        Proceed,
        NextCycle,
        EndSession
    }

    private async Task RunTradingAsync(
        string runId,
        RunMode mode,
        string profileName,
        string? payload,
        CancellationToken cancellationToken)
    {
        var request = string.IsNullOrWhiteSpace(payload)
            ? new TradingReplayRequest()
            : JsonSerializer.Deserialize<TradingReplayRequest>(payload, PayloadJsonOptions)
              ?? throw new InvalidDataException("The trading replay payload is empty.");
        if (mode == RunMode.Live && !request.ArmLiveInput)
        {
            SetStatus(
                WorkerState.NeedsAttention,
                BotKind.TradingPost,
                mode,
                profileName,
                "Live input is not armed",
                "No orders are sent. Enable 'Arm live input' on the Session tab to allow clicks and keys.");
            return;
        }

        var session = await StartTradingSessionAsync(runId, mode, profileName, request, cancellationToken);
        if (session is null)
        {
            return;
        }

        // Alongside the launcher: every character's bags take 13-15 s to read and are only needed at Character Select, but
        // from 22 Nov LOG IN waited for them.
        session.StockBeforeLaunch = Task.Run(() => ReadFlipStockBeforeLaunchAsync(session, cancellationToken), CancellationToken.None);
        if (session.LiveDriver is { } launchDriver)
        {
            launchDriver.BeforeCharacterChoice = token => session.StockBeforeLaunch.WaitAsync(token);
        }

        // A sleeping PC, display power-off or lock screen stops capture and input to the game.
        using var keepAwake = session.LiveDriver is not null && _liveTemplateMatcher is null ? new KeepAwake() : null;

        if (session.LiveDriver is not null)
        {
            _logger.LogInformation("Opening the game: looking for the launcher, Character Select or the game world.");
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (await RunTradingCycleAsync(session, cancellationToken) == CycleStep.EndSession)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            session.EndWith("Stopped by User", "Trading session stopped by operator.");
            throw;
        }
        catch (Exception ex)
        {
            session.EndWith("Faulted", ex.Message);
            session.Report.RecordIncident(_utcNow(), "Error", "Worker Exception", ex.Message, recovered: false);
            throw;
        }
        finally
        {
            await FinishTradingSessionAsync(session);
        }
    }

    private async Task<CycleStep> RunTradingCycleAsync(TradingSession session, CancellationToken cancellationToken)
    {
        RefillGameRestartAllowance(session);

        if (IsStopRequested)
        {
            session.EndWith("Stopped by User", "Stopped after finishing the current step.");
            _logger.LogInformation("Stopped after finishing the current step.");
            return CycleStep.EndSession;
        }

        if (IsPauseRequested)
        {
            await Task.Delay(100, cancellationToken);
            return CycleStep.NextCycle;
        }

        if (session.Cap.IsExpired(_utcNow()))
        {
            session.CapReached = true;
            session.Outcome = "Time Cap Reached";
            session.OutcomeSummary = await FinalizeSessionOnCapAsync(session).ConfigureAwait(false);
            return CycleStep.EndSession;
        }

        MarkStep(session, "pause");
        var driver = session.LiveDriver;
        if (driver is not null)
        {
            var opened = await OpenTradingPostAndCollectDeliveryAsync(session, driver, cancellationToken);
            await session.StockBeforeLaunch; // done with the bags before the cycle reads them again
            MarkStep(session, "open and collect");
            if (opened != CycleStep.Proceed)
            {
                return opened;
            }
        }

        if (IsPauseRequested) return CycleStep.NextCycle;

        var portfolioGuidance = await RefreshTradeHistoryAsync(session, cancellationToken);
        if (session.Request.EnableInventorySellPass)
        {
            await SyncOwnedFlipInventoryFromApiAsync(session, cancellationToken).ConfigureAwait(false);
        }

        await RescanShoppingListIfDueAsync(session, cancellationToken);
        var cycleCandidates = await CandidatesForCycleAsync(session, portfolioGuidance, cancellationToken);
        MarkStep(session, "sync and plan");

        if (IsPauseRequested) return CycleStep.NextCycle;

        SetStatusIfNotPaused(WorkerState.ProcessingTrading, BotKind.TradingPost, session.Mode, session.ProfileName, WorkerStatus.EvaluatingMarket, "Running a trading cycle.");
        var result = await session.Controller.RunCycleAsync(
            session.RunId,
            cycleCandidates,
            session.Blacklist,
            session.Policy,
            cancellationToken,
            () => IsPauseRequested);
        RecordCycleResult(session, result);
        await AdjustPaceAsync(session, result, cancellationToken).ConfigureAwait(false);
        MarkStep(session, "buy");

        if (IsPauseRequested)
        {
            SetStatusIfNotPaused(WorkerState.Paused, BotKind.TradingPost, session.Mode, session.ProfileName, "Paused", "Pause requested; no new actions will begin.");
            return CycleStep.NextCycle;
        }

        var soldCount = 0;
        if (driver is not null && session.Request.EnableInventorySellPass)
        {
            var listed = await RunSellPassAsync(session, driver, cancellationToken);
            MarkStep(session, "sell");
            if (listed is not { } listedCount)
            {
                return CycleStep.NextCycle;
            }

            soldCount = listedCount;
        }

        if (IsPauseRequested) return CycleStep.NextCycle;

        var canceledOutbidBuys = 0;
        if (driver is not null && session.Request.EnableOutbidBuyRefresh && result.Market is not null)
        {
            canceledOutbidBuys = await RefreshOutbidBuysAsync(session, driver, result.Market.Transactions, cancellationToken);
            MarkStep(session, "cancel");
        }

        if (IsPauseRequested) return CycleStep.NextCycle;

        if (driver is not null && session.Client.RelistStaleUndercutSells && result.Market is not null)
        {
            await RelistStaleUndercutSellsAsync(session, driver, result.Market.Transactions, cancellationToken).ConfigureAwait(false);
            MarkStep(session, "relist");
        }

        if (IsPauseRequested) return CycleStep.NextCycle;

        var now = _utcNow();
        var (swapped, characterSwapDue) = await SwapCharacterIfDueAsync(session, now, cancellationToken);
        MarkStep(session, "swap");
        if (swapped != CycleStep.Proceed)
        {
            return swapped;
        }

        ReportCycleStatus(session, result, portfolioGuidance, soldCount, canceledOutbidBuys, characterSwapDue, now);
        await SaveCycleMetricsAsync(session, result, cancellationToken).ConfigureAwait(false);

        var recovered = await RecoverFailedCycleAsync(session, result, cancellationToken);
        if (recovered != CycleStep.Proceed)
        {
            return recovered;
        }

        await WaitForNextCycleAsync(session, cancellationToken);
        return CycleStep.NextCycle;
    }

    private void RefillGameRestartAllowance(TradingSession session)
    {
        if (session.GameRestartsUsed > 0 && _utcNow() - session.LastGameRestartUtc > GameRestartBudgetRefill)
        {
            // Disconnects on long Asia->EU nights are normal; the restart allowance is per 2 quiet hours,
            // not per whole session, so a 10-hour run is not halted by its third disconnect.
            _logger.LogInformation("No game restart for {Hours}h; restart allowance refilled ({Max}).", GameRestartBudgetRefill.TotalHours, session.MaxGameRestarts);
            session.GameRestartsUsed = 0;
        }
    }

    private async Task<CycleStep> OpenTradingPostAndCollectDeliveryAsync(
        TradingSession session,
        Gw2TradingPostUiDriver driver,
        CancellationToken cancellationToken)
    {
        var (tpOpen, tpMsg) = await driver.EnsureTradingPostOpenAsync(cancellationToken);
        if (!tpOpen)
        {
            var (recovered, recoverMsg) = await RecoverLiveClientAsync(
                session,
                driver,
                tpMsg,
                "Trading Post Recovery",
                reasonInFailureReport: true,
                cancellationToken);
            if (!recovered)
            {
                return HaltUnlessPaused(session, "Trading Post unavailable", recoverMsg);
            }
        }

        if (IsPauseRequested) return CycleStep.NextCycle;

        var deliveryOutcome = await CollectSmartDeliveryAsync(session, driver, cancellationToken);
        if (deliveryOutcome.Collected)
        {
            _logger.LogDebug("Delivery pickup complete: {Coins} copper, {Items} flip item(s) queued.", deliveryOutcome.CoinsCollectedCopper, deliveryOutcome.CollectedFlipItemNames.Count);
        }

        return CycleStep.Proceed;
    }

    /// <summary>Syncs the account's trade history (every 45 min) and reloads it (every 5 min); returns the portfolio guidance.</summary>
    private async Task<PortfolioGuidance?> RefreshTradeHistoryAsync(TradingSession session, CancellationToken cancellationToken)
    {
        var request = session.Request;
        var nowForLedger = _utcNow();
        // Keep the Income & Portfolio ledger current during long sessions: without this, winner boosts and
        // loser guards stay frozen at the last manual "Sync GW2 Account".
        if (request.UsePortfolioIntelligence &&
            _ledgerStore is not null &&
            _marketData is Gw2OfficialDataProvider ledgerSource &&
            nowForLedger - session.LastLedgerSyncUtc >= LedgerSyncInterval)
        {
            session.LastLedgerSyncUtc = nowForLedger;
            try
            {
                var synced = await ledgerSource.SyncPortfolioAsync(_ledgerStore, request.ActiveAccountName, cancellationToken).ConfigureAwait(false);
                session.LedgerLoadedAtUtc = DateTimeOffset.MinValue; // re-read below
                session.LastLedgerSyncNote = $"synced {nowForLedger:HH:mm} UTC";
                _logger.LogInformation(
                    "Portfolio sync: {New} new trade(s) archived ({Total} total); history-based boosts and guards refreshed.",
                    synced.NewlyArchivedTransactions,
                    synced.TotalArchivedTransactions);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                session.LastLedgerSyncNote = "sync failed";
                _logger.LogWarning("Portfolio sync failed ({Message}); using the last synced history.", ex.Message);
            }
        }

        if ((request.UsePortfolioIntelligence || request.EnableInventorySellPass) &&
            _ledgerStore is not null &&
            (session.LedgerHistory.Count == 0 || (nowForLedger - session.LedgerLoadedAtUtc) >= TimeSpan.FromMinutes(5)))
        {
            try
            {
                session.LedgerHistory = await _ledgerStore.GetTransactionsAsync(request.ActiveAccountName, cancellationToken).ConfigureAwait(false);
                session.LedgerLoadedAtUtc = nowForLedger;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Could not load SQLite ledger for portfolio intelligence.");
            }
        }

        return request.UsePortfolioIntelligence
            ? PortfolioProfitAdvisor.BuildGuidance(
                session.LedgerHistory,
                session.LastKnownActiveOrders,
                nowForLedger,
                request.DefaultOrderQuantity ?? 1)
            : null;
    }

    private async Task RescanShoppingListIfDueAsync(TradingSession session, CancellationToken cancellationToken)
    {
        var request = session.Request;
        if (request.AutoRescanFilter is not { } rescanFilter ||
            request.AutoRescanIntervalMinutes <= 0 ||
            _marketScanner is null ||
            _utcNow() - session.LastRescanUtc < TimeSpan.FromMinutes(request.AutoRescanIntervalMinutes))
        {
            return;
        }

        session.LastRescanUtc = _utcNow();
        if (await RescanShoppingListAsync(rescanFilter, session.Blacklist, session.Candidates.Count, request.CandidatesPath, cancellationToken).ConfigureAwait(false) is { } fresh)
        {
            session.LastRescanCount = fresh.Count;
            // The Custom Shopping List (sent inline) stays in the session; only the scanned part is replaced.
            session.Candidates = MergeWithCustomList(request.EffectiveCandidates, fresh);
            session.CandidateNames = CandidateNameSet(fresh);
        }
    }

    /// <summary>
    /// Sets the pace of the next cycle from what this one left undone, and looks for more items when the list has run out
    /// with gold idle.
    /// </summary>
    private async Task AdjustPaceAsync(TradingSession session, TradingCycleResult result, CancellationToken cancellationToken)
    {
        if (result.Market?.Balance is not { } balance || !result.Succeeded)
        {
            return;
        }

        var orders = result.Market.Transactions;
        static long Value(Transaction order) => Math.Max(0, order.PriceCopper) * Math.Max(1, order.Quantity);
        var buys = orders.Where(order => order.Type == TransactionType.Buy).Sum(Value);
        var listed = (long)(orders.Where(order => order.Type == TransactionType.Sell).Sum(Value) * 0.85m);
        session.GoldCapReached = result.Decisions.Any(decision => decision.Action is null && decision.Reason?.StartsWith(TradingCycleController.GoldCapReason, StringComparison.Ordinal) == true);
        var pressure = new CyclePressure(
            result.Decisions.Count(decision => decision.Action is null && decision.Reason == TradingCycleController.PerCycleLimitReason),
            result.Decisions.Count(decision => decision.Action is not null),
            session.GoldCapReached,
            Math.Max(0, balance.Copper - session.MinBalanceCopper),
            buys,
            balance.Copper + buys + listed);
        if (session.Boost.Update(pressure, allowed: !string.Equals(session.ProfileName, "safe", StringComparison.OrdinalIgnoreCase)))
        {
            var message = session.Boost.Active
                ? $"Faster pace: {pressure.Waiting} items wait and {MarketScanItem.FormatCoins(pressure.SpendableCopper)} is idle, so up to {session.PerPass} orders per cycle and a {(int)session.Boost.Pause(session.Interval).TotalSeconds} s pause."
                : "Back to the normal pace: no backlog, or not enough free gold.";
            _logger.LogInformation("{Message}", message);
            RecordEvent(session, "pace", session.Boost.Active ? "faster" : "normal", detail: $"{pressure.Waiting} waiting", activity: message);
        }

        session.Controller.MaxActionsPerCycle = session.PerPass;
        if (session.Boost.TakeWiderScan(_utcNow()))
        {
            await WidenShoppingListAsync(session, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Adds items that trade half as often as the scanner's filter asks, because every item on the list has an order.
    /// They stay until the next regular re-scan.
    /// </summary>
    private async Task WidenShoppingListAsync(TradingSession session, CancellationToken cancellationToken)
    {
        if (session.Request.AutoRescanFilter is not { } filter || _marketScanner is null)
        {
            return;
        }

        var wider = filter with { MinimumDailyTrades = Math.Max(5, filter.MinimumDailyTrades / 2), MaxResults = filter.MaxResults * 2 };
        _logger.LogInformation("Every item on the list has an order and gold is idle: scanning for items with {Trades:0}+ trades/day.", wider.MinimumDailyTrades);
        if (await RescanShoppingListAsync(wider, session.Blacklist, session.Candidates.Count, candidatesPath: null, cancellationToken).ConfigureAwait(false) is not { } found)
        {
            return;
        }

        var before = session.Candidates.Count;
        session.Candidates = MergeWithCustomList(session.Candidates, found);
        session.CandidateNames.UnionWith(CandidateNameSet(found));
        RecordEvent(session, "pace", "wider-scan", detail: $"{before} -> {session.Candidates.Count}",
            activity: $"The list ran out with gold idle: added {session.Candidates.Count - before} items that trade less often");
    }

    /// <summary>Items outbid again and again in the last day without a fill start the session paused.</summary>
    private async Task PauseYesterdaysBotWarsAsync(RunMode mode, CancellationToken cancellationToken)
    {
        if (_metricsStore is null || _ledgerStore is null || mode != RunMode.Live)
        {
            return;
        }

        try
        {
            var since = _utcNow().AddHours(-24);
            var outbid = await _metricsStore.CountByItemAsync("cancel", "outbid", since, cancellationToken).ConfigureAwait(false);
            var filled = (await _ledgerStore.GetTransactionsAsync(null, cancellationToken).ConfigureAwait(false))
                .Where(row => row.Kind == TransactionType.Buy && row.PurchasedAt >= since)
                .Select(row => row.ItemName);
            var paused = _buyCompetition.SeedFromLastDay(outbid, filled, _utcNow());
            if (paused.Count > 0)
            {
                _logger.LogInformation("Paused from the start ({Hours} h): outbid {Min}+ times in the last day without a fill: {Items}.",
                    (int)BuyCompetitionTracker.SeededPause.TotalHours, BuyCompetitionTracker.OutbidsBeforeSeededPause, string.Join(", ", paused));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not read the last day's outbid counts.");
        }
    }

    /// <summary>The shopping list with live prices, minus items paused because another bot keeps outbidding us.</summary>
    private async Task<IReadOnlyList<ItemCandidate>> CandidatesForCycleAsync(
        TradingSession session,
        PortfolioGuidance? portfolioGuidance,
        CancellationToken cancellationToken)
    {
        var cycleCandidates = await RefreshCycleCandidatesAsync(
            session.Candidates,
            session.ResolvedItemIds,
            session.ProfitRefresher,
            session.Policy,
            session.Request.OverbidBufferMinCopper,
            session.Request.UndercutBufferMinCopper,
            portfolioGuidance,
            cancellationToken);
        var cycleNow = _utcNow();
        if (_buyCompetition.PausedItems(cycleNow).Count > 0)
        {
            cycleCandidates = cycleCandidates.Where(c => !_buyCompetition.IsPaused(c.ItemName, cycleNow)).ToArray();
        }

        // Sell before buying again: an item still unsold in some character's bags is not bought again until listed.
        if (session.Request.EnableInventorySellPass)
        {
            var withoutStock = CharacterStock.WithoutHeldStock(cycleCandidates, session.HeldFlipItems, session.Request.DefaultOrderQuantity ?? 1);
            var waiting = cycleCandidates.Count - withoutStock.Count;
            if (waiting > 0 && waiting != session.BuysWaitingOnStock)
            {
                _logger.LogInformation(
                    "Not buying {Count} item(s) still unsold in bags until they are listed: {Items}.",
                    waiting,
                    string.Join(", ", cycleCandidates.Except(withoutStock).Select(c => c.ItemName).Take(12)));
            }

            session.BuysWaitingOnStock = waiting;
            cycleCandidates = withoutStock;
        }

        return cycleCandidates;
    }

    /// <summary>Adds the cycle's decisions, wallet, actions and errors to the session report and remembers the order book.</summary>
    private void RecordCycleResult(TradingSession session, TradingCycleResult result)
    {
        var report = session.Report;
        report.RecordCycle(result.Decisions.Count);
        if (result.Market is not null)
        {
            report.RecordWalletSnapshot(
                result.Market.Balance?.Copper,
                result.Market.Transactions.Count);
        }

        foreach (var actionResult in result.Actions)
        {
            var actionType = actionResult.Action.Kind switch
            {
                TradingActionKind.PlaceBuyOrder => "Buy Order",
                TradingActionKind.PlaceSellOrder => "Sell Listing",
                TradingActionKind.CancelBuyOrder => "Cancel Outbid",
                TradingActionKind.CancelSellOrder => "Cancel Sell",
                _ => actionResult.Action.Kind.ToString()
            };
            RecordBuyEvent(session, actionResult);
            report.RecordTrade(
                actionResult.CompletedAt,
                actionType,
                actionResult.Action.ItemName,
                actionResult.Action.Quantity,
                actionResult.Action.PriceCopper,
                actionResult.Status.ToString(),
                actionResult.Message);
            if (actionResult.Status == TradingActionStatus.Failed)
            {
                report.RecordIncident(
                    actionResult.CompletedAt,
                    "Warning",
                    "Order Action",
                    $"{actionResult.Action.ItemName}: {actionResult.Message}",
                    recovered: false);
            }
        }

        var outbidPastCeiling = result.Actions.Where(BuyCompetitionTracker.IsOutbidPastCeiling).Select(a => a.Action.ItemName).ToArray();
        foreach (var paused in _buyCompetition.RecordOutbidCancels(outbidPastCeiling, _utcNow()))
        {
            _logger.LogInformation(
                "Pausing buys of {Item} for {Hours} h: outbid past our profitable price {Count} times without a single fill (another bot is camping it).",
                paused,
                (int)_buyCompetition.PausedFor(paused, _utcNow()).TotalHours,
                BuyCompetitionTracker.OutbidCancelsBeforePause);
        }

        if (result.Market?.Transactions is { Count: > 0 } marketTransactions)
        {
            session.LastKnownActiveOrders = marketTransactions;
        }
        else if (result.Succeeded && result.Market is not null)
        {
            session.LastKnownActiveOrders = result.Market.Transactions;
        }

        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            report.RecordIncident(
                _utcNow(),
                result.Succeeded ? "Warning" : "Error",
                "Trading Cycle",
                result.Error,
                recovered: false);
        }
    }

    /// <summary>
    /// Lists queued flip items from the bags at a profitable price. Returns how many were listed, or null when a
    /// pause came in after the Delivery Box pickup.
    /// </summary>
    private async Task<int?> RunSellPassAsync(TradingSession session, Gw2TradingPostUiDriver driver, CancellationToken cancellationToken)
    {
        var request = session.Request;
        var maxPerPass = session.PerPass;
        // A big backlog on this character is listed faster; the buy cycle waits a little longer meanwhile.
        if (session.FlipStockByCharacter.FirstOrDefault(entry => CharacterStock.NamesMatch(entry.Name, driver.CurrentCharacterName)).Items >= CharacterStock.BacklogItemsForBiggerSellPass)
        {
            maxPerPass *= 2;
        }

        if (ReadyTargetsHere(session, driver, maxPerPass).Count == 0)
        {
            return 0;
        }

        await CollectSmartDeliveryAsync(session, driver, cancellationToken);
        if (IsPauseRequested)
        {
            return null;
        }

        var readyTargets = ReadyTargetsHere(session, driver, maxPerPass);
        IReadOnlyDictionary<string, SellPricing> sellPricing;
        (readyTargets, sellPricing) = await KeepProfitableSellTargetsAsync(readyTargets, session.Candidates, request.UndercutBufferMinCopper, cancellationToken).ConfigureAwait(false);
        sellPricing = WithOwnListings(sellPricing, session.LastKnownActiveOrders);
        if (readyTargets.Count == 0)
        {
            return 0;
        }

        var sellResults = await driver.SellInventoryPassDetailedAsync(
            readyTargets,
            maxPerPass,
            request.UndercutBufferMinCopper,
            request.UndercutBufferMaxCopper,
            cancellationToken,
            sellPricing);
        var postSellNow = _utcNow();
        session.SellQueue.RecordSellPassResults(sellResults, postSellNow);
        var soldCount = 0;
        foreach (var outcome in sellResults)
        {
            _logger.LogInformation("Sell pass {Outcome} for {Item}: {Details}", outcome.Outcome, outcome.ItemName, outcome.Details ?? "-");
            RecordSellEvent(session, outcome);
            if (outcome.Outcome == SellPassItemOutcome.Listed)
            {
                soldCount++;
                session.Report.RecordTrade(
                    postSellNow,
                    "Sell Listing",
                    outcome.ItemName,
                    UnitsHere(session, driver, outcome.ItemName),
                    outcome.UnitCopper ?? 0,
                    "Completed",
                    outcome.Details ?? "Sell order listed from bags.");
            }
        }

        return soldCount;
    }

    /// <summary>Size of the listed stack: the dialog lists all of it (1 when the bags were not read yet).</summary>
    private static long UnitsHere(TradingSession session, Gw2TradingPostUiDriver driver, string item) =>
        Math.Max(1, session.FlipUnitsByCharacter
            .FirstOrDefault(pair => CharacterStock.NamesMatch(pair.Key, driver.CurrentCharacterName)).Value?
            .GetValueOrDefault(item.Trim()) ?? 1);

    /// <summary>
    /// Queued sell targets this character holds (or that no bag shows yet) and whose stack here is all Mmopal's, up to
    /// the per-pass limit.
    /// </summary>
    private IReadOnlyList<string> ReadyTargetsHere(TradingSession session, Gw2TradingPostUiDriver driver, int maxPerPass) =>
        KeepMmopalStacks(
                session,
                CharacterStock.ListableHere(
                    session.SellQueue.GetReadyTargets(_utcNow(), int.MaxValue),
                    session.FlipItemsByCharacter,
                    driver.CurrentCharacterName),
                driver.CurrentCharacterName)
            .Take(maxPerPass)
            .ToArray();

    /// <summary>Cancels outbid buy orders so the next cycle can bid again, and pauses items another bot keeps camping.</summary>
    private async Task<int> RefreshOutbidBuysAsync(
        TradingSession session,
        Gw2TradingPostUiDriver driver,
        IReadOnlyList<Transaction> orders,
        CancellationToken cancellationToken)
    {
        // Only Mmopal's own buy orders are cancelled; orders placed by hand stay, outbid or old.
        var placed = (await _store.GetPlacedBuyOrdersAsync(_utcNow().AddDays(-60), cancellationToken).ConfigureAwait(false))
            .Select(order => new PlacedBuyOrder(order.ItemName, order.PlacedAt, order.UnitCopper))
            .ToArray();
        // The driver still sees the hand-placed orders: a search that lists one of them next to ours is not safe.
        var canceledOutbidBuys = await driver.CancelOutbidBuyOrdersAsync(
            orders,
            session.PerPass,
            order => OwnedStock.IsMmopalsOrder(order, placed),
            cancellationToken,
            session.GoldCapReached);
        _buyCompetition.PauseSlowItems(driver.LastCanceledStaleItems, _utcNow());
        foreach (var paused in _buyCompetition.RecordOutbidCancels(driver.LastCanceledOutbidItems, _utcNow()))
        {
            _logger.LogInformation(
                "Pausing buys of {Item} for {Hours} h: outbid {Count} times without a single fill (another bot is camping it); its order slots go to items that fill.",
                paused,
                (int)_buyCompetition.PausedFor(paused, _utcNow()).TotalHours,
                BuyCompetitionTracker.OutbidCancelsBeforePause);
        }

        foreach (var (item, outcome, note) in driver.LastCanceledOutbidItems.Select(item => (item, "outbid", "outbid"))
                     .Concat(driver.LastCanceledStaleItems.Select(item => (item, "unfilled", "unfilled for a day"))))
        {
            RecordEvent(session, "cancel", outcome, item, activity: $"Cancelled the buy order for {item} ({note})");
            session.Report.RecordTrade(_utcNow(), "Outbid Refresh", item, 1, 0, "Canceled", $"Buy order cancelled: {note}.");
        }

        RecordUnconfirmedCancels(session, driver);

        return canceledOutbidBuys;
    }

    private void RecordUnconfirmedCancels(TradingSession session, Gw2TradingPostUiDriver driver)
    {
        foreach (var item in driver.TakeUnconfirmedCancels())
        {
            RecordEvent(session, "cancel", "unconfirmed", item);
        }
    }

    /// <summary>
    /// Swaps to another parked character when the AFK schedule says so, or early when the Delivery Box has been
    /// locked on this character for too long. Returns whether a swap was due (for the status event).
    /// </summary>
    private async Task<(CycleStep Step, bool SwapDue)> SwapCharacterIfDueAsync(
        TradingSession session,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var client = session.Client;
        var deliveryStuckSwap = IsDeliveryStuckSwapDue(now, client.ParkedCharacterCount);
        var stockSwapTarget = StockSwapTarget(session, now);
        if (!session.AfkSwap.IsDue(now) && !deliveryStuckSwap && stockSwapTarget is null)
        {
            return (CycleStep.Proceed, false);
        }

        if (session.LiveDriver is { } driver && client.ParkedCharacterCount > 1)
        {
            if (deliveryStuckSwap)
            {
                _logger.LogInformation(
                    "Swapping character early: the Delivery Box has been locked on {Character} for {Minutes} min (it is not standing next to a Black Lion trader).",
                    driver.CurrentCharacterLabel ?? "this character",
                    (int)(now - _deliveryLockedSinceUtc!.Value).TotalMinutes);
            }
            else if (stockSwapTarget is not null && !session.AfkSwap.IsDue(now))
            {
                _logger.LogInformation(
                    "Swapping to {Target} to list its unsold flip items ({Stock}); listing works anywhere, but only from the logged-in character's bags.",
                    stockSwapTarget,
                    DescribeFlipStock(session.FlipStockByCharacter));
            }

            // Any swap goes to the character holding the most unsold stock when its name can be read.
            var preferredNames = session.Request.EnableInventorySellPass
                ? ParkedFlipStock(session, driver)
                    .Where(entry => !CharacterStock.NamesMatch(entry.Name, driver.CurrentCharacterName))
                    .Select(entry => entry.Name)
                    .ToArray()
                : [];

            _lastCharacterSwapUtc = now;
            _deliveryLockedSinceUtc = null;
            var (swapped, swapMsg) = await driver.RotateParkedCharacterAsync(
                session.AfkSwap.RandomizeCharacter,
                client.ParkedCharacterCount,
                cancellationToken,
                preferredNames);
            if (swapped)
            {
                session.Report.RecordCharacterSwap(_utcNow(), true, swapMsg);
                RecordEvent(
                    session,
                    "swap",
                    deliveryStuckSwap ? "delivery-locked" : stockSwapTarget is not null && !session.AfkSwap.IsDue(now) ? "stock" : "schedule",
                    detail: swapMsg,
                    activity: $"Swapped to {driver.CurrentCharacterLabel ?? "another character"}");
                session.SellQueue.CharacterChanged(_utcNow());
            }
            else
            {
                var (recovered, recoverMsg) = await RecoverLiveClientAsync(
                    session,
                    driver,
                    swapMsg,
                    "Character Swap Recovery",
                    reasonInFailureReport: false,
                    cancellationToken);
                session.Report.RecordCharacterSwap(_utcNow(), recovered, recovered ? recoverMsg : swapMsg);
                if (!recovered)
                {
                    return (HaltUnlessPaused(session, "Character swap failed", recoverMsg), true);
                }
            }
        }
        else
        {
            _logger.LogInformation("AFK character swap is due ({Summary}); advancing next schedule.", session.AfkSwap.Describe(now));
            session.Report.RecordCharacterSwap(now, true, "Simulated/single-character AFK rotation interval ticked.");
        }

        session.AfkSwap.MarkSwapped(now);
        return (CycleStep.Proceed, true);
    }

    /// <summary>
    /// Another character worth a swap only to list its stock (see <see cref="CharacterStock.StockSwapTarget"/>),
    /// at most once per <see cref="MinTimeBetweenDeliverySwaps"/>: every logout risks the server disconnect that
    /// repeated swaps caused.
    /// </summary>
    private string? StockSwapTarget(TradingSession session, DateTimeOffset now)
    {
        if (session.LiveDriver is not { } driver ||
            !session.Request.EnableInventorySellPass ||
            session.Client.ParkedCharacterCount <= 1)
        {
            return null;
        }

        // Normally at most once an hour. Once this character has nothing left to list, move on after the 20 minutes
        // that keep logouts safe: on 22 Nov stock sat on all five characters while each swap waited the full hour.
        var sinceSwap = now - _lastCharacterSwapUtc;
        var nothingLeftHere = ReadyTargetsHere(session, driver, 1).Count == 0 &&
                              session.FlipStockByCharacter.FirstOrDefault(entry => CharacterStock.NamesMatch(entry.Name, driver.CurrentCharacterName)).Items == 0;
        return sinceSwap >= MinTimeBetweenDeliverySwaps || (nothingLeftHere && sinceSwap >= MinTimeBetweenRecoverySwaps)
            ? CharacterStock.StockSwapTarget(ParkedFlipStock(session, driver), driver.CurrentCharacterName)
            : null;
    }

    /// <summary>
    /// Flip stock on characters in the rotation. Until a name-guided swap has read the parked portraits every
    /// character counts; a target outside the rotation is simply not found and the usual slot is used.
    /// </summary>
    private static IReadOnlyList<(string Name, long Items)> ParkedFlipStock(TradingSession session, Gw2TradingPostUiDriver driver) =>
        driver.ParkedCharacterNames.Count == 0
            ? session.FlipStockByCharacter
            : session.FlipStockByCharacter
                .Where(entry => driver.ParkedCharacterNames.Any(parked => CharacterStock.NamesMatch(entry.Name, parked)))
                .ToArray();

    private static string DescribeFlipStock(IReadOnlyList<(string Name, long Items)> stock) =>
        stock.Count == 0 ? "none" : string.Join(", ", stock.Select(entry => $"{entry.Name} {entry.Items}"));

    /// <summary>Sets the status line and sends the Health event the Session page shows.</summary>
    private void ReportCycleStatus(
        TradingSession session,
        TradingCycleResult result,
        PortfolioGuidance? portfolioGuidance,
        int soldCount,
        int canceledOutbidBuys,
        bool characterSwapDue,
        DateTimeOffset now)
    {
        var request = session.Request;
        var observation = session.ObservationReader?.LastRead?.Observation;
        var buyPausedLowGold = result.Market?.Balance is { } bal &&
                               (bal.Copper <= 0 || (session.MinBalanceCopper > 0 && bal.Copper <= session.MinBalanceCopper));
        var executedActions = result.Actions.Count(a => a.Status != TradingActionStatus.Skipped);
        var skippedActions = result.Actions.Count - executedActions;
        var baseSummary = result.Error ?? (session.Candidates.Count == 0
            ? "No candidates supplied; 0 decisions evaluated."
            : skippedActions > 0
                ? $"Decisions: {result.Decisions.Count}; actions: {executedActions} ({skippedActions} skipped)."
                : $"Decisions: {result.Decisions.Count}; actions: {executedActions}.");
        var extras = new List<string>();
        if (soldCount > 0) extras.Add($"listed {soldCount} sell");
        if (canceledOutbidBuys > 0) extras.Add($"refreshed {canceledOutbidBuys} outbid buy");
        if (buyPausedLowGold) extras.Add("Buys paused (0g/reserve floor)");
        var summaryMessage = extras.Count > 0
            ? $"{baseSummary} ({string.Join(", ", extras)})"
            : baseSummary;

        SetStatusIfNotPaused(
            result.Succeeded ? WorkerState.TradingPostReady : WorkerState.Recovering,
            BotKind.TradingPost,
            session.Mode,
            session.ProfileName,
            result.Succeeded ? WorkerStatus.CycleComplete : "Cycle requires recovery",
            summaryMessage);
        Emit(new WorkerEvent(
            WorkerEventTypes.Health,
            JsonSerializer.Serialize(new
            {
                state = result.FinalState.ToString(),
                succeeded = result.Succeeded,
                decisions = result.Decisions.Count,
                actions = executedActions,
                skippedActions,
                soldCount,
                canceledOutbidBuys,
                pendingSellCount = session.SellQueue.Count,
                buyPausedLowGold,
                source = request.Source,
                observation = observation?.Status.ToString(),
                ui = observation?.Ui,
                visiblePrice = observation?.VisiblePrice,
                timings = observation?.TimingsMilliseconds,
                orderBook = OrderBookReport.Summarize(result.Market?.Transactions).Describe(),
                characterSwap = session.AfkSwap.Describe(now),
                characterSwapDue,
                nextCharacterSwapAt = session.AfkSwap.NextSwapAt,
                sessionCap = session.Cap.Describe(now),
                marketList = DescribeMarketList(request, session.Candidates.Count, session.LastRescanCount, session.LastRescanUtc, now) +
                             (_buyCompetition.PausedItems(now).Count is var botWar and > 0 ? $" · {botWar} paused (outbid, no fills)" : string.Empty) +
                             (session.BuysWaitingOnStock > 0 ? $" · {session.BuysWaitingOnStock} waiting to sell stock" : string.Empty),
                portfolio = DescribePortfolio(request, portfolioGuidance, session.LastLedgerSyncNote),
                sessionStartedAt = session.Cap.StartedAt,
                sessionExpiresAt = session.Cap.ExpiresAt,
                parkedCharacters = session.Client.ParkedCharacterCount,
                error = result.Error,
                timestamp = now
            },
            PayloadJsonOptions)));
    }

    /// <summary>
    /// After a failed live cycle: recover the client, or halt once failures in a row exceed what recovery and
    /// restarts could still fix.
    /// </summary>
    private async Task<CycleStep> RecoverFailedCycleAsync(TradingSession session, TradingCycleResult result, CancellationToken cancellationToken)
    {
        if (result.Succeeded)
        {
            session.ConsecutiveFailedCycles = 0;
            return CycleStep.Proceed;
        }

        if (session.LiveDriver is not { } driver)
        {
            return CycleStep.Proceed;
        }

        session.ConsecutiveFailedCycles++;
        var maxUnrecoveredBudget = Math.Max(3, session.MaxRecoveryRetries + session.MaxGameRestarts + 2);
        if (session.ConsecutiveFailedCycles >= maxUnrecoveredBudget)
        {
            session.EndWith("Needs Attention", result.Error ?? $"Cycle failed {session.ConsecutiveFailedCycles} times consecutively.");
            HaltWithNeedsAttention(
                session.Mode,
                session.ProfileName,
                session.Request.Source,
                "Recovery limit reached",
                session.OutcomeSummary);
            return CycleStep.EndSession;
        }

        if (session.MaxRecoveryRetries > 0 || session.GameRestartsUsed < session.MaxGameRestarts)
        {
            var (recovered, recoverMsg) = await RecoverLiveClientAsync(
                session,
                driver,
                result.Error ?? "Trading cycle failed",
                "Cycle Recovery",
                reasonInFailureReport: false,
                cancellationToken);
            if (!recovered)
            {
                return HaltUnlessPaused(session, "Recovery failed", recoverMsg);
            }
        }

        return CycleStep.Proceed;
    }

    /// <summary>Waits the scan interval, cut short so the session cap is handled on time.</summary>
    private async Task WaitForNextCycleAsync(TradingSession session, CancellationToken cancellationToken)
    {
        if (session.Cap.IsExpired(_utcNow()))
        {
            return;
        }

        var waitDuration = session.Boost.Pause(session.Interval);
        if (session.Cap.ExpiresAt is { } expiresAt)
        {
            var remainingBeforeCap = expiresAt - _utcNow();
            if (remainingBeforeCap <= TimeSpan.Zero)
            {
                return;
            }

            if (remainingBeforeCap < waitDuration)
            {
                waitDuration = remainingBeforeCap;
            }
        }

        await WaitIntervalOrPauseAsync(waitDuration, cancellationToken);
    }
}
