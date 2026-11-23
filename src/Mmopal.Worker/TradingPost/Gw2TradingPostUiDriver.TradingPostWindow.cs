using Microsoft.Extensions.Logging;
using Mmopal.Contracts;
using Mmopal.Gw2;
using Mmopal.Native;
using Mmopal.Worker.Vision;

namespace Mmopal.Worker;

/// <summary>Opening and recovering the Trading Post window, in-game popups, and the Delivery Box.</summary>
public sealed partial class Gw2TradingPostUiDriver
{
    public async Task<(bool Ready, string Message)> TryRecoverTradingSessionAsync(CancellationToken cancellationToken = default)
    {
        if (IsPaused)
        {
            return (false, "Paused by operator during session recovery.");
        }

        var okButton = await TryFindOnceAsync("ok.png", CaptureRegion.FullScreen1024x768, 0.90m, cancellationToken).ConfigureAwait(false);
        if (okButton is not null)
        {
            _logger.LogWarning("Dismissing in-game dialog ('ok.png') at ({X}, {Y}) during recovery.", CenterX(okButton), CenterY(okButton));
            await ClickCenterAndSettleAsync(okButton, cancellationToken).ConfigureAwait(false);
        }

        await EnsureGameWindowAsync(cancellationToken).ConfigureAwait(false);
        await CloseItemWindowAsync(cancellationToken).ConfigureAwait(false);

        if (!await IsTradingPostOpenAndActiveAsync(cancellationToken).ConfigureAwait(false) &&
            !await TrySwitchFromBlackLionToTradingPostAsync(cancellationToken).ConfigureAwait(false))
        {
            var frame = await _screenshots.CaptureAsync(CaptureRegion.FullScreen1024x768, cancellationToken).ConfigureAwait(false);
            if (await TryAdvanceLauncherOrCharacterSelectAsync(cancellationToken).ConfigureAwait(false))
            {
                await SettleAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (DetectLauncherWhiteCard(frame) is null &&
                     await FindCharacterSelectPlayAsync(frame, cancellationToken).ConfigureAwait(false) is null)
            {
                // Clear stuck in-game overlays (esc menu / tooltips / focus loss) before retrying F/O.
                await PressEscapeClosingGameMenuAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return await EnsureTradingPostOpenAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Clicks OK on an in-game message box (e.g. the frequent "unable to gain access to the log-in server ...
    /// Code=42:..." notice on Asia→EU routes, which does not affect trading) and logs its text. Returns true when one
    /// was dismissed. Call before starting a UI step; the popup is modal and swallows clicks meant for the TP.
    /// </summary>
    private async Task<bool> DismissTransientPopupAsync(string context, CancellationToken cancellationToken)
    {
        var okButton = await TryFindOnceAsync("ok.png", CaptureRegion.FullScreen1024x768, 0.90m, cancellationToken).ConfigureAwait(false);
        if (okButton is null)
        {
            return false;
        }

        var lines = await ReadTextVariantsAsync(PopupTextRegion, cancellationToken).ConfigureAwait(false);
        var message = lines.Count == 0 ? "(text unreadable)" : string.Join(" ", lines.Skip(lines.Count / 2).Take(3));
        var normalized = NormalizeItemName(string.Join(" ", lines));
        if (FuzzyContains(normalized, "lostitsconnection") || FuzzyContains(normalized, "waitafewminutes"))
        {
            // "The game client lost its connection to the server. Please wait a few minutes before restarting"
            // (Code=7:...). Reconnecting at once risks another kick; recovery waits first.
            ServerDisconnectSeenUtc = _utcNow();
        }

        _logger.LogInformation("Dismissed in-game popup during {Context}: {Message}", context, message);
        await ClickCenterAsync(okButton, cancellationToken).ConfigureAwait(false);
        await SettleShortAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<(bool Ready, string Message)> EnsureTradingPostOpenAsync(CancellationToken cancellationToken = default)
    {
        for (var dismissals = 0; await HasErrorPopupAsync(cancellationToken).ConfigureAwait(false); dismissals++)
        {
            if (dismissals >= 2)
            {
                return (false, "In-game error dialog ('ok.png') keeps reappearing after OK; handing over to recovery.");
            }

            await DismissTransientPopupAsync("Trading Post check", cancellationToken).ConfigureAwait(false);
            await SettleAsync(cancellationToken).ConfigureAwait(false);
        }

        await EnsureGameWindowAsync(cancellationToken).ConfigureAwait(false);
        if (await IsTradingPostOpenAndActiveAsync(cancellationToken).ConfigureAwait(false))
        {
            return (true, "Trading Post is open.");
        }

        if (await TrySwitchFromBlackLionToTradingPostAsync(cancellationToken).ConfigureAwait(false))
        {
            return (true, "Switched Black Lion window to Trading Post tab.");
        }

        if (await TryAdvanceLauncherOrCharacterSelectAsync(cancellationToken).ConfigureAwait(false))
        {
            await EnsureGameWindowAsync(cancellationToken).ConfigureAwait(false);
            if (await IsTradingPostOpenAndActiveAsync(cancellationToken).ConfigureAwait(false) ||
                await TrySwitchFromBlackLionToTradingPostAsync(cancellationToken).ConfigureAwait(false))
            {
                return (true, "Entered world and Trading Post is open.");
            }
        }

        var interactKey = string.IsNullOrWhiteSpace(_settings.InteractKeybind) ? "F" : _settings.InteractKeybind.Trim();
        var tpKey = string.IsNullOrWhiteSpace(_settings.TradingPostKeybind) ? "O" : _settings.TradingPostKeybind.Trim();

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsPaused)
            {
                return (false, "Paused by operator while opening Trading Post.");
            }

            if (await DismissTransientPopupAsync("opening the Trading Post", cancellationToken).ConfigureAwait(false) &&
                await HasErrorPopupAsync(cancellationToken).ConfigureAwait(false))
            {
                return (false, "In-game error dialog appeared while opening the Trading Post and did not close.");
            }

            if (await IsLauncherStillActiveAsync(cancellationToken).ConfigureAwait(false))
            {
                _ = await TryAdvanceLauncherOrCharacterSelectAsync(cancellationToken).ConfigureAwait(false);
                if (await IsLauncherStillActiveAsync(cancellationToken).ConfigureAwait(false))
                {
                    return (false, "GW2 Launcher is still open; waiting for login and game client launch before pressing in-game keys.");
                }
            }

            var currentFrame = await _screenshots.CaptureAsync(CaptureRegion.FullScreen1024x768, cancellationToken).ConfigureAwait(false);
            if (await FindCharacterSelectPlayAsync(currentFrame, cancellationToken).ConfigureAwait(false) is not null)
            {
                _logger.LogInformation("Character Select detected during TP open check (attempt {Attempt}/3); advancing into world.", attempt);
                _ = await TryAdvanceLauncherOrCharacterSelectAsync(cancellationToken).ConfigureAwait(false);
            }

            await EnsureGameWindowAsync(cancellationToken).ConfigureAwait(false);

            if (attempt > 1)
            {
                // Whatever the last key opened instead (a bank or other NPC next to the trader, an NPC dialog) stays
                // on top and swallows the next F; close it first.
                await CloseStrayWindowsAsync(attempt, cancellationToken).ConfigureAwait(false);
            }

            // F, then O (Black Lion panel works anywhere; its sidebar scales icon switches Gem Store -> Trading Post),
            // then F again.
            var useTpFallback = attempt == 2 && !string.Equals(tpKey, interactKey, StringComparison.OrdinalIgnoreCase);
            var clickedInGameFocus = false;
            if (!useTpFallback)
            {
                var interactPrompt = await TryFindOnceAsync("interact.png", InGameInteractRegion, 0.68m, cancellationToken).ConfigureAwait(false)
                                     ?? await TryFindOnceAsync("interact.png", CaptureRegion.FullScreen1024x768, 0.72m, cancellationToken).ConfigureAwait(false);
                if (interactPrompt is not null)
                {
                    _logger.LogInformation("Clicking visible 'Talk [F]' prompt at ({X}, {Y}) (attempt {Attempt}/3).", CenterX(interactPrompt), CenterY(interactPrompt), attempt);
                    await ClickCenterAsync(interactPrompt, cancellationToken).ConfigureAwait(false);
                    clickedInGameFocus = true;
                    await Task.Delay(Math.Min(150, Math.Max(10, _settleMs / 4)), cancellationToken).ConfigureAwait(false);
                }
            }

            if (!clickedInGameFocus && _requireTemplateFilesOnDisk)
            {
                await _input.ClickAsync(DeterministicRestPoint.X, DeterministicRestPoint.Y, cancellationToken).ConfigureAwait(false);
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }

            var keyToPress = useTpFallback ? tpKey : interactKey;
            _logger.LogInformation("Opening Trading Post (attempt {Attempt}/3 with key '{Key}').", attempt, keyToPress);
            await _input.KeyPressAsync(keyToPress, cancellationToken).ConfigureAwait(false);

            if (await WaitForTradingPostOrSidebarAsync(cancellationToken).ConfigureAwait(false) &&
                (await IsTradingPostOpenAndActiveAsync(cancellationToken).ConfigureAwait(false) ||
                 await TrySwitchFromBlackLionToTradingPostAsync(cancellationToken).ConfigureAwait(false)))
            {
                LastTradingPostOpenFailed = false;
                return (true, $"Trading Post opened via '{keyToPress}' on attempt {attempt}.");
            }
        }

        LastTradingPostOpenFailed = true;
        return (false, $"Trading Post did not open after 3 bounded attempts using '{interactKey}'/'{tpKey}' (Black Lion sidebar included). Another NPC may be closer than the trader; confirm this character is parked right next to a TP NPC.");
    }

    /// <summary>Esc closes a bank/NPC window or dialog; a second Esc closes the game menu if the first opened it.</summary>
    private async Task CloseStrayWindowsAsync(int attempt, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Closing any non-TP window (bank, NPC dialog) before attempt {Attempt}/3.", attempt);
        await PressEscapeClosingGameMenuAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Presses Esc once, and again when that opened the Game Menu (its Log Out button is showing).</summary>
    private async Task PressEscapeClosingGameMenuAsync(CancellationToken cancellationToken)
    {
        await _input.KeyPressAsync("escape", cancellationToken).ConfigureAwait(false);
        await SettleShortAsync(cancellationToken).ConfigureAwait(false);
        if (await TryFindOnceAsync("logout.png", CaptureRegion.FullScreen1024x768, 0.74m, cancellationToken).ConfigureAwait(false) is not null)
        {
            await _input.KeyPressAsync("escape", cancellationToken).ConfigureAwait(false);
            await SettleShortAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Clicks Take All. Handles the two live cases that left the box untouched: the panel collapsed under a longer
    /// category list (Take All hidden; the header is clicked to expand it), and a TP opened away from a Black Lion
    /// trader, where the box says "Speak to a Black Lion Trading Company NPC ..." and Take All collects nothing
    /// (<see cref="DeliveryNeedsTraderNpc"/> is set and nothing is clicked).
    /// </summary>
    public async Task<bool> CollectDeliveryBoxAsync(CancellationToken cancellationToken = default)
    {
        DeliveryNeedsTraderNpc = false;
        DeliveryLooksEmpty = false;
        await DismissTransientPopupAsync("Delivery Box", cancellationToken).ConfigureAwait(false);
        var takeAll = await TryFindOnceAsync("take_all.png", CaptureRegion.FullScreen1024x768, 0.72m, cancellationToken).ConfigureAwait(false);
        if (takeAll is null && _textLocator is not null)
        {
            if (await FindDeliveryHeaderAsync(cancellationToken).ConfigureAwait(false) is { } point)
            {
                _logger.LogInformation("Delivery Box panel is collapsed; expanding it to reach Take All.");
                await _input.ClickAsync(point.X, point.Y, cancellationToken).ConfigureAwait(false);
                await SettleAsync(cancellationToken).ConfigureAwait(false);
                takeAll = await TryFindOnceAsync("take_all.png", CaptureRegion.FullScreen1024x768, 0.72m, cancellationToken).ConfigureAwait(false);
            }
        }

        if (takeAll is null)
        {
            _logger.LogInformation("Delivery Box has something to collect but Take All is not visible.");
            return false;
        }

        if (_textLocator is not null)
        {
            var panelText = NormalizeItemName(string.Join(" ", await ReadTextVariantsAsync(DeliveryPanelRegion, cancellationToken).ConfigureAwait(false)));
            if (FuzzyContains(panelText, "whenyoubuyorsell"))
            {
                // An empty box explains itself with the same "speak to a Black Lion Trading Company NPC" words. It was read
                // as locked while the API still showed a box collected minutes before: 26 TP reopens (22 min) on 22-23 Nov.
                DeliveryLooksEmpty = true;
                _logger.LogInformation("Delivery Box is empty on screen; the API still shows what was already collected.");
                return false;
            }

            if (FuzzyContains(panelText, "blackliontradingcompanynpc") || FuzzyContains(panelText, "pickupyouritems"))
            {
                DeliveryNeedsTraderNpc = true;
                _logger.LogWarning("Delivery Box says to speak to a Black Lion Trading Company NPC: the TP was opened away from a trader, so Take All would collect nothing.");
                return false;
            }
        }

        await ClickCenterAndSettleAsync(takeAll, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Closes the TP and opens it again through the nearest NPC (F first), so the Delivery Box unlocks.</summary>
    public async Task<(bool Ready, string Message)> ReopenTradingPostAtTraderAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Reopening the Trading Post through the trader NPC so the Delivery Box can be collected.");
        await CloseAllTpWindowsAsync(cancellationToken).ConfigureAwait(false);
        await SettleShortAsync(cancellationToken).ConfigureAwait(false);
        return await EnsureTradingPostOpenAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<(int X, int Y)?> FindDeliveryHeaderAsync(CancellationToken cancellationToken)
    {
        try
        {
            var frame = await _screenshots.CaptureAsync(CaptureRegion.FullScreen1024x768, cancellationToken).ConfigureAwait(false);
            if (frame.Pixels.Length == 0 || _textLocator is null)
            {
                return null;
            }

            var lines = await _textLocator.LocateLinesAsync(frame, DeliveryPanelRegion, cancellationToken).ConfigureAwait(false);
            var header = lines.FirstOrDefault(line => FuzzyContains(NormalizeItemName(line.Text), "deliverybox"));
            // Collapsed, the header sits at the bottom (y~681); expanded it is at y~475, where a click would collapse it.
            return header is null || header.Y < 600 ? null : (header.X + header.Width / 2, header.Y + header.Height / 2);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogDebug(exception, "Could not look for the Delivery Box header.");
            return null;
        }
    }

    private async Task<TemplateMatch?> OpenItemOrderWindowAsync(
        TemplateMatch firstItem,
        string anchorTemplate,
        decimal minScore,
        CancellationToken cancellationToken)
    {
        await _input.ClickAsync(CenterX(firstItem), CenterY(firstItem) + 38, cancellationToken).ConfigureAwait(false);
        var firstWaitMs = _requireTemplateFilesOnDisk ? Math.Min(_uiWaitMs, 2_000) : _uiWaitMs;
        var anchor = await WaitForTemplateAsync(anchorTemplate, CaptureRegion.FullScreen1024x768, minScore, firstWaitMs, cancellationToken).ConfigureAwait(false);
        if (anchor is null && _requireTemplateFilesOnDisk)
        {
            // If the search box only lost focus on click 1, click Row 1 once more before the full wait budget.
            await _input.ClickAsync(CenterX(firstItem) + 20, CenterY(firstItem) + 38, cancellationToken).ConfigureAwait(false);
            anchor = await WaitForTemplateAsync(anchorTemplate, CaptureRegion.FullScreen1024x768, minScore, _uiWaitMs, cancellationToken).ConfigureAwait(false);
        }

        return anchor;
    }

    private async Task<TemplateMatch?> FindPriceSpinnerArrowAsync(string templateFileName, CancellationToken cancellationToken)
    {
        var scoped = await TryFindOnceAsync(templateFileName, ModalPriceSpinnerRegion, 0.75m, cancellationToken).ConfigureAwait(false);
        if (scoped is not null || _requireTemplateFilesOnDisk)
        {
            return scoped;
        }

        return await TryFindOnceAsync(templateFileName, new CaptureRegion(550, 0, 1024 - 550, 768), 0.75m, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> IsTradingPostOpenAndActiveAsync(CancellationToken cancellationToken)
    {
        var frame = await _screenshots.CaptureAsync(TopHeaderRegion, cancellationToken).ConfigureAwait(false);

        if (!_requireTemplateFilesOnDisk)
        {
            var tpTab = await TryFindInFrameAsync(frame, "tradingpost_tab.png", TopHeaderRegion, 0.72m, cancellationToken).ConfigureAwait(false);
            if (tpTab is not null && tpTab.Y < 240)
            {
                return true;
            }
        }

        return await TryFindInFrameAsync(frame, "transactions.png", TransactionsTabRegion, 0.70m, cancellationToken).ConfigureAwait(false) is not null ||
               await TryFindInFrameAsync(frame, "buy_items_tab_open.png", BuyTabRegion, 0.65m, cancellationToken).ConfigureAwait(false) is not null ||
               await TryFindInFrameAsync(frame, "buy_items_tab.png", BuyTabRegion, 0.68m, cancellationToken).ConfigureAwait(false) is not null ||
               await TryFindInFrameAsync(frame, "items_tab.png", SellTabRegion, 0.68m, cancellationToken).ConfigureAwait(false) is not null ||
               await TryFindInFrameAsync(frame, "cog.png", SearchFieldRegion, 0.70m, cancellationToken).ConfigureAwait(false) is not null ||
               await TryFindInFrameAsync(frame, "search.png", SearchFieldRegion, 0.70m, cancellationToken).ConfigureAwait(false) is not null ||
               await TryFindInFrameAsync(frame, "exit_item_window.png", ModalCloseButtonRegion, 0.75m, cancellationToken).ConfigureAwait(false) is not null;
    }

    private async Task<bool> TrySwitchFromBlackLionToTradingPostAsync(CancellationToken cancellationToken)
    {
        var sidebarTp = await TryFindOnceAsync("tradingpost_tab.png", LeftSidebarTpTabRegion, 0.68m, cancellationToken).ConfigureAwait(false);
        if (sidebarTp is not null)
        {
            _logger.LogInformation("Switching Black Lion window from Gem Store to Trading Post tab via sidebar icon.");
            await ClickCenterAndSettleAsync(sidebarTp, cancellationToken).ConfigureAwait(false);
            return await WaitForTradingPostTabAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!_requireTemplateFilesOnDisk ||
            await TryFindOnceAsync("black_lion.png", BlackLionCrestRegion, 0.75m, cancellationToken).ConfigureAwait(false) is null)
        {
            return false;
        }

        // The Black Lion window is open but its sidebar icon was not matched: click the icon's measured position.
        await _input.ClickAsync(DeterministicLeftSidebarTpPoint.X, DeterministicLeftSidebarTpPoint.Y, cancellationToken).ConfigureAwait(false);
        await SettleAsync(cancellationToken).ConfigureAwait(false);
        return await WaitForTradingPostTabAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// After the sidebar switch the TP tab can take seconds to load on high ping. A single check used to fail, the
    /// opening loop then pressed Esc and fell back to O, and an O-opened TP leaves the Delivery Box locked
    /// ("speak to a Black Lion Trading Company NPC"). So poll for the whole UI wait before giving up.
    /// </summary>
    private async Task<bool> WaitForTradingPostTabAsync(CancellationToken cancellationToken)
    {
        var deadline = _utcNow() + TimeSpan.FromMilliseconds(_uiWaitMs);
        while (true)
        {
            if (await IsTradingPostOpenAndActiveAsync(cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            if (_utcNow() >= deadline || IsPaused)
            {
                return false;
            }

            await Task.Delay(Math.Max(_pollMs, 200), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> WaitForTradingPostOrSidebarAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(_uiWaitMs);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsPaused) return false;
            if (await IsTradingPostOpenAndActiveAsync(cancellationToken).ConfigureAwait(false) ||
                await TryFindOnceAsync("tradingpost_tab.png", LeftSidebarTpTabRegion, 0.68m, cancellationToken).ConfigureAwait(false) is not null)
            {
                return true;
            }

            if (DateTimeOffset.UtcNow >= deadline || IsPaused)
            {
                return false;
            }

            await Task.Delay(_pollMs, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WaitForInWorldAsync(CancellationToken cancellationToken)
    {
        var waitMs = _requireTemplateFilesOnDisk
            ? Math.Max(_uiWaitMs * 4, 30_000)
            : Math.Max(_uiWaitMs * 2, 120);
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(waitMs);
        while (DateTimeOffset.UtcNow < deadline && !IsPaused)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var charScreenStillUp = await TryFindOnceAsync("charscreen_play.png", CharSelectPlayRegion, 0.72m, cancellationToken).ConfigureAwait(false);
            if (charScreenStillUp is null &&
                (await TryFindOnceAsync("ingame_1.png", TopHeaderRegion, 0.72m, cancellationToken).ConfigureAwait(false) is not null ||
                 await TryFindOnceAsync("interact.png", InGameInteractRegion, 0.68m, cancellationToken).ConfigureAwait(false) is not null ||
                 await IsTradingPostOpenAndActiveAsync(cancellationToken).ConfigureAwait(false)))
            {
                return;
            }

            await Task.Delay(_pollMs, cancellationToken).ConfigureAwait(false);
        }
    }
}
