using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mmopal.Contracts;
using Mmopal.Gw2;
using Mmopal.Infrastructure;
using Mmopal.Native;

namespace Mmopal.Worker;

/// <summary>Recovery: retries, character swaps, game restarts, and finishing the session at the time cap.</summary>
public sealed partial class WorkerRuntime
{
    /// <summary>Recovery may switch parked character at most once per <see cref="MinTimeBetweenRecoverySwaps"/>.</summary>
    private bool CanSwapCharacterForRecovery(DateTimeOffset nowUtc) => nowUtc - _lastCharacterSwapUtc >= MinTimeBetweenRecoverySwaps;

    /// <summary>
    /// The session cap: one last Delivery Box / bags / listing pass, a final wallet snapshot, then log out and close
    /// the game (per settings). Returns the summary for the report.
    /// </summary>
    private async Task<string> FinalizeSessionOnCapAsync(TradingSession session)
    {
        var (mode, profileName, request, profile) = (session.Mode, session.ProfileName, session.Request, session.Profile);
        var effectiveClientSettings = session.Client;
        var liveDriver = session.LiveDriver;
        var pendingSellTracker = session.SellQueue;
        var reportAccumulator = session.Report;
        _logger.LogInformation("Max session duration cap reached; finalizing, rechecking, logging out, and stopping cleanly.");
        SetStatus(
            WorkerState.Stopping,
            BotKind.TradingPost,
            mode,
            profileName,
            "Finalizing session cap",
            "Max session duration reached; rechecking Delivery Box/listings, logging out, and closing Guild Wars 2.");

        using var finalizeCts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = finalizeCts.Token;

        if (liveDriver is not null)
        {
            try
            {
                if (request.EnableInventorySellPass)
                {
                    session.LastInventoryApiSyncUtc = DateTimeOffset.MinValue;
                    await SyncOwnedFlipInventoryFromApiAsync(session, ct).ConfigureAwait(false);
                }

                await CollectSmartDeliveryAsync(session, liveDriver, ct).ConfigureAwait(false);

                if (request.EnableInventorySellPass)
                {
                    var finalTargets = pendingSellTracker.GetReadyTargets(_utcNow(), Math.Max(1, profile.MaxOverbidUpdatesPerPass));
                    IReadOnlyDictionary<string, SellPricing> finalPricing;
                    finalTargets = KeepMmopalStacks(session, finalTargets, liveDriver.CurrentCharacterName);
                    (finalTargets, finalPricing) = await KeepProfitableSellTargetsAsync(finalTargets, request.EffectiveCandidates, request.UndercutBufferMinCopper, ct).ConfigureAwait(false);
                    finalPricing = WithOwnListings(finalPricing, session.LastKnownActiveOrders);
                    if (finalTargets.Count > 0)
                    {
                        var sellResults = await liveDriver.SellInventoryPassDetailedAsync(
                            finalTargets,
                            Math.Max(1, profile.MaxOverbidUpdatesPerPass),
                            request.UndercutBufferMinCopper,
                            request.UndercutBufferMaxCopper,
                            ct,
                            finalPricing).ConfigureAwait(false);
                        pendingSellTracker.RecordSellPassResults(sellResults, _utcNow());
                        foreach (var s in sellResults.Where(r => r.Outcome == SellPassItemOutcome.Listed))
                        {
                            reportAccumulator.RecordTrade(_utcNow(), "Sell Listing", s.ItemName, 1, 0, "Completed", "Listed during final cap recheck.");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Final delivery/listing recheck interrupted before logout.");
            }
        }

        if (_marketData is not null)
        {
            try
            {
                var finalBal = await _marketData.GetBalanceAsync(ct).ConfigureAwait(false);
                var finalTx = await _marketData.GetTransactionsAsync(ct).ConfigureAwait(false);
                reportAccumulator.RecordWalletSnapshot(finalBal?.Copper, finalTx.Count);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Final API wallet snapshot unavailable during cap shutdown.");
            }
        }

        var gameClosedNote = "Game left open per settings.";
        if (effectiveClientSettings.AutoCloseGameOnSessionCap &&
            (liveDriver is not null || string.Equals(request.Source, "screen", StringComparison.OrdinalIgnoreCase)))
        {
            if (liveDriver is not null)
            {
                try
                {
                    var (loggedOut, logoutMsg) = await liveDriver.GracefulLogoutAndCloseGameAsync(ct).ConfigureAwait(false);
                    _logger.LogInformation("In-game cap logout ({LoggedOut}): {Message}", loggedOut, logoutMsg);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "In-game logout step skipped before process close.");
                }
            }

            try
            {
                var closeResult = await _clientPreparer.CloseGameAsync(effectiveClientSettings, CancellationToken.None).ConfigureAwait(false);
                gameClosedNote = closeResult.Message;
            }
            catch (Exception ex)
            {
                gameClosedNote = $"Close game attempt failed: {ex.Message}";
                reportAccumulator.RecordIncident(_utcNow(), "Warning", "Game Close", gameClosedNote, recovered: false);
            }
        }

        await _store.CompleteRunAsync(session.RunId, WorkerState.Stopped.ToString(), CancellationToken.None).ConfigureAwait(false);
        var stoppedSummary = $"Max session cap reached — finalized, rechecked, and stopped cleanly. ({gameClosedNote})";
        SetStatus(
            WorkerState.Stopped,
            BotKind.TradingPost,
            mode,
            profileName,
            "Session cap reached",
            stoppedSummary);
        return stoppedSummary;
    }

    private void HaltWithNeedsAttention(
        RunMode mode,
        string profileName,
        string source,
        string lastAction,
        string message)
    {
        SetStatus(
            WorkerState.NeedsAttention,
            BotKind.TradingPost,
            mode,
            profileName,
            lastAction,
            message);
        Emit(new WorkerEvent(
            WorkerEventTypes.Health,
            JsonSerializer.Serialize(new
            {
                state = WorkerState.NeedsAttention.ToString(),
                succeeded = false,
                source,
                error = message,
                timestamp = _utcNow()
            },
            PayloadJsonOptions)));
    }

    /// <summary>
    /// Recovers the live client (in place, by another character, or by a game restart within the budget) and
    /// records the attempt in the report. A failed Trading Post recovery keeps the original error in the report.
    /// </summary>
    private async Task<(bool Recovered, string Message)> RecoverLiveClientAsync(
        TradingSession session,
        Gw2TradingPostUiDriver liveDriver,
        string reason,
        string reportTitle,
        bool reasonInFailureReport,
        CancellationToken cancellationToken)
    {
        var restartsBefore = session.GameRestartsUsed;
        var (recovered, message) = await TryRecoverOrRestartLiveClientAsync(
            liveDriver,
            session.Client,
            session.Mode,
            session.ProfileName,
            reason,
            session.MaxRecoveryRetries,
            session.MaxGameRestarts,
            () => session.GameRestartsUsed,
            used => SetGameRestartsUsed(session, used),
            cancellationToken);
        session.Report.RecordRecoveryAttempt(
            _utcNow(),
            reportTitle,
            !recovered && reasonInFailureReport ? $"{reason} -> {message}" : message,
            recovered,
            isGameRestart: session.GameRestartsUsed > restartsBefore);
        return (recovered, message);
    }

    /// <summary>Ends the session for the operator to look at; a pause during recovery instead waits for Resume.</summary>
    private CycleStep HaltUnlessPaused(TradingSession session, string lastAction, string message)
    {
        if (IsPauseRequested)
        {
            return CycleStep.NextCycle;
        }

        session.EndWith("Needs Attention", message);
        HaltWithNeedsAttention(session.Mode, session.ProfileName, session.Request.Source, lastAction, message);
        return CycleStep.EndSession;
    }

    private async Task<(bool Recovered, string Message)> TryRecoverOrRestartLiveClientAsync(
        Gw2TradingPostUiDriver liveDriver,
        Gw2ClientSettings settings,
        RunMode mode,
        string profileName,
        string initialError,
        int maxRecoveryRetries,
        int maxGameRestarts,
        Func<int> getGameRestartsUsed,
        Action<int> setGameRestartsUsed,
        CancellationToken cancellationToken)
    {
        var lastError = initialError;
        var isLiveTemplates = _liveTemplateMatcher is null;

        if (liveDriver.ServerDisconnectSeenUtc is { } disconnectedAt && _utcNow() - disconnectedAt < TimeSpan.FromMinutes(10))
        {
            // GW2 asks to wait a few minutes after a server disconnect; logging straight back in risks another kick.
            liveDriver.ServerDisconnectSeenUtc = null;
            var wait = isLiveTemplates ? ServerDisconnectCooldown : TimeSpan.FromMilliseconds(5);
            _logger.LogWarning("Disconnected by the server; waiting {Minutes} minutes before reconnecting.", (int)wait.TotalMinutes);
            SetStatusIfNotPaused(WorkerState.Recovering, BotKind.TradingPost, mode, profileName, "Waiting after disconnect", $"Server disconnect; reconnecting in {(int)wait.TotalMinutes} min.");
            await WaitIntervalOrPauseAsync(wait, cancellationToken).ConfigureAwait(false);
            if (IsPauseRequested)
            {
                return (false, "Paused by operator while waiting after a server disconnect.");
            }
        }

        for (var retry = 1; retry <= maxRecoveryRetries; retry++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsPauseRequested)
            {
                return (false, "Paused by operator during session recovery.");
            }

            var message = $"Recovering GW2 session (retry {retry}/{maxRecoveryRetries}): {initialError}";
            _logger.LogWarning("{Message}", message);
            SetStatusIfNotPaused(
                WorkerState.Recovering,
                BotKind.TradingPost,
                mode,
                profileName,
                $"Recovering ({retry}/{maxRecoveryRetries})",
                message);

            var delay = isLiveTemplates
                ? TimeSpan.FromSeconds(Math.Min(6, 2 * retry))
                : TimeSpan.FromMilliseconds(5);
            await WaitIntervalOrPauseAsync(delay, cancellationToken).ConfigureAwait(false);
            if (IsPauseRequested)
            {
                return (false, "Paused by operator during session recovery.");
            }

            bool ok;
            string recoverMsg;
            if (retry == 1 && liveDriver.LastTradingPostOpenFailed && settings.ParkedCharacterCount > 1 &&
                CanSwapCharacterForRecovery(_utcNow()))
            {
                _lastCharacterSwapUtc = _utcNow();
                // Every key was tried on this character (e.g. a bank NPC next to the trader keeps winning F), so
                // retrying in place or restarting into the same character repeats the failure. Switch characters.
                _logger.LogWarning("Trading Post would not open on this character; switching to another parked character first.");
                (ok, recoverMsg) = await liveDriver.RotateParkedCharacterAsync(
                    settings.RandomizeCharacterSelection,
                    settings.ParkedCharacterCount,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                (ok, recoverMsg) = await liveDriver.TryRecoverTradingSessionAsync(cancellationToken).ConfigureAwait(false);
            }

            if (ok)
            {
                _sessionSellTracker?.CharacterChanged(_utcNow());
                var okText = $"Recovered GW2 Trading Post on retry {retry}/{maxRecoveryRetries}: {recoverMsg}";
                _logger.LogInformation("{Message}", okText);
                return (true, okText);
            }

            lastError = recoverMsg;
        }

        var restartsUsed = getGameRestartsUsed();
        if (restartsUsed < maxGameRestarts)
        {
            restartsUsed++;
            setGameRestartsUsed(restartsUsed);
            var restartNotice = $"In-place recovery exhausted ({maxRecoveryRetries} retries); restarting Guild Wars 2 (restart {restartsUsed}/{maxGameRestarts}).";
            _logger.LogWarning("{Message}", restartNotice);
            SetStatusIfNotPaused(
                WorkerState.Recovering,
                BotKind.TradingPost,
                mode,
                profileName,
                $"Restarting GW2 ({restartsUsed}/{maxGameRestarts})",
                restartNotice);

            var restarted = await _clientPreparer.RestartAsync(settings, cancellationToken).ConfigureAwait(false);
            if (IsPauseRequested)
            {
                return (false, "Paused by operator during GW2 restart.");
            }

            if (restarted.WindowReady)
            {
                _ = await liveDriver.TryAdvanceLauncherOrCharacterSelectAsync(cancellationToken).ConfigureAwait(false);
                var (reopened, reopenMsg) = await liveDriver.EnsureTradingPostOpenAsync(cancellationToken).ConfigureAwait(false);
                if (reopened)
                {
                    _sessionSellTracker?.CharacterChanged(_utcNow());
                    _deliveryLockedSinceUtc = null;
                    var recoveredAfterRestart = $"Recovered session after restarting Guild Wars 2 ({restartsUsed}/{maxGameRestarts}).";
                    _logger.LogInformation("{Message}", recoveredAfterRestart);
                    return (true, recoveredAfterRestart);
                }

                lastError = reopenMsg;
            }
            else
            {
                lastError = restarted.Message;
            }
        }

        return (false, $"Session halted after {maxRecoveryRetries} recovery retry(s) and {restartsUsed}/{maxGameRestarts} game restart(s): {lastError}");
    }
}
