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

        // A sleeping PC, display power-off or lock screen stops capture and input to the game.
        using var keepAwake = session.LiveDriver is not null && _liveTemplateMatcher is null ? new KeepAwake() : null;

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

        var driver = session.LiveDriver;
        if (driver is not null)
        {
            var opened = await OpenTradingPostAndCollectDeliveryAsync(session, driver, cancellationToken);
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

        if (IsPauseRequested) return CycleStep.NextCycle;

        SetStatusIfNotPaused(WorkerState.ProcessingTrading, BotKind.TradingPost, session.Mode, session.ProfileName, "Evaluating market", "Running a deterministic trading cycle.");
        var result = await session.Controller.RunCycleAsync(
            session.RunId,
            cycleCandidates,
            session.Blacklist,
            session.Policy,
            cancellationToken,
            () => IsPauseRequested);
        RecordCycleResult(session, result);

        if (IsPauseRequested)
        {
            SetStatusIfNotPaused(WorkerState.Paused, BotKind.TradingPost, session.Mode, session.ProfileName, "Paused", "Pause requested; no new actions will begin.");
            return CycleStep.NextCycle;
        }

        var soldCount = 0;
        if (driver is not null && session.Request.EnableInventorySellPass)
        {
            if (await RunSellPassAsync(session, driver, cancellationToken) is not { } listed)
            {
                return CycleStep.NextCycle;
            }

            soldCount = listed;
        }

        if (IsPauseRequested) return CycleStep.NextCycle;

        var canceledOutbidBuys = 0;
        if (driver is not null && session.Request.EnableOutbidBuyRefresh && result.Market is not null)
        {
            canceledOutbidBuys = await RefreshOutbidBuysAsync(session, driver, result.Market.Transactions, cancellationToken);
        }

        if (IsPauseRequested) return CycleStep.NextCycle;

        if (driver is not null && session.Client.RelistStaleUndercutSells && result.Market is not null)
        {
            await RelistStaleUndercutSellsAsync(driver, result.Market.Transactions, session.CandidateNames, session.Request.UndercutBufferMinCopper, session.Report, cancellationToken).ConfigureAwait(false);
        }

        if (IsPauseRequested) return CycleStep.NextCycle;

        var now = _utcNow();
        var (swapped, characterSwapDue) = await SwapCharacterIfDueAsync(session, now, cancellationToken);
        if (swapped != CycleStep.Proceed)
        {
            return swapped;
        }

        ReportCycleStatus(session, result, portfolioGuidance, soldCount, canceledOutbidBuys, characterSwapDue, now);

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
        var maxPerPass = Math.Max(1, session.Profile.MaxOverbidUpdatesPerPass);
        if (session.SellQueue.GetReadyTargets(_utcNow(), maxPerPass).Count == 0)
        {
            return 0;
        }

        await CollectSmartDeliveryAsync(session, driver, cancellationToken);
        if (IsPauseRequested)
        {
            return null;
        }

        var readyTargets = session.SellQueue.GetReadyTargets(_utcNow(), maxPerPass);
        IReadOnlyDictionary<string, SellPricing> sellPricing;
        (readyTargets, sellPricing) = await KeepProfitableSellTargetsAsync(readyTargets, session.Candidates, request.UndercutBufferMinCopper, cancellationToken).ConfigureAwait(false);
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
            if (outcome.Outcome == SellPassItemOutcome.Listed)
            {
                soldCount++;
                session.Report.RecordTrade(
                    postSellNow,
                    "Sell Listing",
                    outcome.ItemName,
                    1,
                    0,
                    "Completed",
                    outcome.Details ?? "Sell order listed from bags.");
            }
        }

        return soldCount;
    }

    /// <summary>Cancels outbid buy orders so the next cycle can bid again, and pauses items another bot keeps camping.</summary>
    private async Task<int> RefreshOutbidBuysAsync(
        TradingSession session,
        Gw2TradingPostUiDriver driver,
        IReadOnlyList<Transaction> orders,
        CancellationToken cancellationToken)
    {
        var canceledOutbidBuys = await driver.CancelOutbidBuyOrdersAsync(
            orders,
            Math.Max(1, session.Profile.MaxOverbidUpdatesPerPass),
            cancellationToken);
        foreach (var paused in _buyCompetition.RecordOutbidCancels(driver.LastCanceledOutbidItems, _utcNow()))
        {
            _logger.LogInformation(
                "Pausing buys of {Item} for {Hours} h: outbid {Count} times without a single fill (another bot is camping it); its order slots go to items that fill.",
                paused,
                (int)BuyCompetitionTracker.PauseDuration.TotalHours,
                BuyCompetitionTracker.OutbidCancelsBeforePause);
        }

        if (canceledOutbidBuys > 0)
        {
            session.Report.RecordTrade(
                _utcNow(),
                "Outbid Refresh",
                $"{canceledOutbidBuys} outbid buy order(s)",
                canceledOutbidBuys,
                0,
                "Canceled",
                "Cleared outbid buy order(s) for fresh bidding.");
        }

        return canceledOutbidBuys;
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
    private string? StockSwapTarget(TradingSession session, DateTimeOffset now) =>
        session.LiveDriver is { } driver &&
        session.Request.EnableInventorySellPass &&
        session.Client.ParkedCharacterCount > 1 &&
        now - _lastCharacterSwapUtc >= MinTimeBetweenDeliverySwaps
            ? CharacterStock.StockSwapTarget(ParkedFlipStock(session, driver), driver.CurrentCharacterName)
            : null;

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
            result.Succeeded ? "Cycle complete" : "Cycle requires recovery",
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
                             (_buyCompetition.PausedItems(now).Count is var botWar and > 0 ? $" · {botWar} paused (outbid, no fills)" : string.Empty),
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

        var waitDuration = session.Interval;
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
