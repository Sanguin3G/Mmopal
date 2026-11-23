using Microsoft.Extensions.Logging;
using Mmopal.Contracts;
using Mmopal.Gw2;
using Mmopal.Native;
using Mmopal.Worker.Vision;

namespace Mmopal.Worker;

/// <summary>GW2 launcher and Character Select: LOG IN / PLAY detection, launcher dialogs, getting into the game.</summary>
public sealed partial class Gw2TradingPostUiDriver
{
    public async Task<bool> HasErrorPopupAsync(CancellationToken cancellationToken = default)
    {
        var match = await TryFindOnceAsync("ok.png", CaptureRegion.FullScreen1024x768, 0.90m, cancellationToken).ConfigureAwait(false);
        return match is not null;
    }

    /// <summary>
    /// Detects the GW2 launcher (theme-independent LOG IN / Auto-Play / PLAY, or Steam Auto-Login)
    /// or Character Select screen and advances into the game world. Returns true if an advance action was taken.
    /// </summary>
    public async Task<bool> TryAdvanceLauncherOrCharacterSelectAsync(CancellationToken cancellationToken = default)
    {
        var advanced = false;
        var hadLauncherActivity = false;
        await EnsureGameWindowAsync(cancellationToken).ConfigureAwait(false);
        var frame = await _screenshots.CaptureAsync(CaptureRegion.FullScreen1024x768, cancellationToken).ConfigureAwait(false);

        if (await DismissLauncherDialogsAsync(frame, cancellationToken).ConfigureAwait(false))
        {
            frame = await _screenshots.CaptureAsync(CaptureRegion.FullScreen1024x768, cancellationToken).ConfigureAwait(false);
            advanced = true;
            hadLauncherActivity = true;
        }

        var snapshot = await InspectLauncherFrameAsync(frame, cancellationToken).ConfigureAwait(false);
        var isLauncherWindow = IsLauncherProcessWindowActive();

        (int X, int Y)? triggeredPlayPoint = null;

        if (snapshot.HasAnySignal || isLauncherWindow)
        {
            hadLauncherActivity = true;
            var launcherWaitMs = _requireTemplateFilesOnDisk
                ? Math.Max(Math.Max(_uiWaitMs * 4, _settings.WindowWaitTimeoutSeconds * 1000), 30_000)
                : Math.Max(_uiWaitMs, 100);
            var launcherDeadline = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(launcherWaitMs);
            // The launcher wait used to be silent until a button was clicked: on 22 Nov four starts sat ~2 min each
            // with nothing in the log. Say what was recognised, and why the wait ended without a click.
            _logger.LogInformation(
                "Launcher on screen (launcher window: {Window}; LOG IN {Login}; PLAY {Play}; READY {Ready}; white card {Card}); waiting up to {Seconds} s for LOG IN or PLAY.",
                isLauncherWindow ? "yes" : "no",
                snapshot.LoginPoint is { } lp ? $"at ({lp.X}, {lp.Y})" : "not found",
                snapshot.PlayPoint is { } pp ? $"at ({pp.X}, {pp.Y})" : "not found",
                snapshot.OcrReadyVisible ? "shown" : "not shown",
                snapshot.WhiteCard is null ? "not found" : "found",
                launcherWaitMs / 1000);
            DateTimeOffset? lastLoginClickUtc = null;
            var loginClickAttempts = 0;
            var emptySignalPolls = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsPaused) return advanced;

                if (await DismissLauncherDialogsAsync(frame, cancellationToken).ConfigureAwait(false))
                {
                    frame = await _screenshots.CaptureAsync(CaptureRegion.FullScreen1024x768, cancellationToken).ConfigureAwait(false);
                    snapshot = await InspectLauncherFrameAsync(frame, cancellationToken).ConfigureAwait(false);
                    advanced = true;
                }

                if (IsGame3DWindowActive())
                {
                    break;
                }

                if (snapshot.PlayPoint is { } playPoint &&
                    !snapshot.OcrReadyVisible &&
                    (_settings.IsSteamPlatform || snapshot.LoginPoint is null))
                {
                    // ArenaNet launcher: LOG IN, then PLAY. Steam launcher: signs in with the Steam account by
                    // itself (LOG IN is never clicked), but PLAY still has to be pressed.
                    triggeredPlayPoint = playPoint;
                    break;
                }

                if (!_settings.IsSteamPlatform && snapshot.LoginPoint is { } detectedLoginPoint)
                {
                    var nowUtc = DateTimeOffset.UtcNow;
                    // The LOG IN label stays on screen for several seconds while the login request is in flight.
                    // Retrying faster than that only produces clicks that land on the LATEST NEWS page (opening
                    // the browser) once the launcher switches over, so wait out a realistic login before retrying.
                    var loginRetryCooldownMs = _requireTemplateFilesOnDisk ? 6_000 : 3_500;
                    var cooldownElapsed = !lastLoginClickUtc.HasValue ||
                                          (nowUtc - lastLoginClickUtc.Value).TotalMilliseconds >= loginRetryCooldownMs;
                    (int X, int Y)? loginPoint = null;
                    if (cooldownElapsed && loginClickAttempts < MaxLoginClickAttempts)
                    {
                        if (_requireTemplateFilesOnDisk)
                        {
                            await EnsureGameWindowAsync(cancellationToken).ConfigureAwait(false);
                        }

                        // Re-check on a fresh frame right before clicking: the launcher may have moved on to the
                        // news page while the previous frame was being inspected. This extra capture and read is why
                        // LOG IN takes a moment longer than PLAY; a click on the news page opens the browser.
                        frame = await _screenshots.CaptureAsync(CaptureRegion.FullScreen1024x768, cancellationToken).ConfigureAwait(false);
                        snapshot = await InspectLauncherFrameAsync(frame, cancellationToken).ConfigureAwait(false);

                        if (snapshot.LoginPoint is { } confirmed &&
                            Math.Abs(confirmed.X - detectedLoginPoint.X) <= 16 &&
                            Math.Abs(confirmed.Y - detectedLoginPoint.Y) <= 16)
                        {
                            loginPoint = confirmed;
                        }
                        else
                        {
                            _logger.LogDebug("LOG IN no longer visible at ({X}, {Y}) on a fresh frame; not clicking.", detectedLoginPoint.X, detectedLoginPoint.Y);
                        }
                    }

                    if (loginPoint is { } point)
                    {
                        loginClickAttempts++;
                        _logger.LogInformation(
                            "Launcher LOG IN button detected at ({X}, {Y}); clicking LOG IN with mouse only (attempt {Attempt}/{Max}).",
                            point.X,
                            point.Y,
                            loginClickAttempts,
                            MaxLoginClickAttempts);
                        if (_requireTemplateFilesOnDisk)
                        {
                            // Rest the cursor on the button first so it highlights: an instant jump-and-click was
                            // ignored on 22 Nov (3 clicks at (123, 585), no login), while a hand resting the mouse
                            // there always worked.
                            await _input.MoveAsync(point.X, point.Y, cancellationToken).ConfigureAwait(false);
                            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                        }

                        await _input.ClickAsync(point.X, point.Y, cancellationToken).ConfigureAwait(false);
                        if (_requireTemplateFilesOnDisk)
                        {
                            // Second tap ensures Coherent UI fires LOG IN even if the first tap was consumed
                            // activating the window or unfocusing the default password input caret.
                            await Task.Delay(120, cancellationToken).ConfigureAwait(false);
                            await _input.ClickAsync(point.X, point.Y, cancellationToken).ConfigureAwait(false);
                        }

                        lastLoginClickUtc = DateTimeOffset.UtcNow;
                        advanced = true;

                        // Ensure enough time remains for login + Auto-Play countdown or PLAY button.
                        var minPostLoginDeadline = nowUtc + TimeSpan.FromMilliseconds(_requireTemplateFilesOnDisk ? 25_000 : _uiWaitMs);
                        if (launcherDeadline < minPostLoginDeadline)
                        {
                            launcherDeadline = minPostLoginDeadline;
                        }
                    }
                }
                else if (_settings.IsSteamPlatform && snapshot.LoginPoint is not null)
                {
                    _logger.LogDebug("Steam platform selected; skipping manual LOG IN click and waiting for Auto-Login/PLAY.");
                }

                if (DateTimeOffset.UtcNow >= launcherDeadline)
                {
                    break;
                }

                await Task.Delay(Math.Max(_pollMs, 100), cancellationToken).ConfigureAwait(false);
                if (IsGame3DWindowActive())
                {
                    break;
                }

                // Keep re-snapping the window on every poll so when the "Downloading 0KB" splash HWND
                // closes and the main Launcher HWND opens in the center of the screen, it snaps to (0, 0) immediately.
                await EnsureGameWindowAsync(cancellationToken).ConfigureAwait(false);
                isLauncherWindow = IsLauncherProcessWindowActive();
                if (IsGame3DWindowActive())
                {
                    break;
                }

                frame = await _screenshots.CaptureAsync(CaptureRegion.FullScreen1024x768, cancellationToken).ConfigureAwait(false);
                snapshot = await InspectLauncherFrameAsync(frame, cancellationToken).ConfigureAwait(false);

                if (!isLauncherWindow && !snapshot.HasAnySignal && !snapshot.OcrAutoPlayVisible && !snapshot.OcrReadyVisible)
                {
                    emptySignalPolls++;
                    if (!_requireTemplateFilesOnDisk || emptySignalPolls >= 8)
                    {
                        break;
                    }
                }
                else
                {
                    emptySignalPolls = 0;
                }
            }

            if (triggeredPlayPoint is null && !IsGame3DWindowActive())
            {
                _logger.LogWarning(
                    "Launcher wait ended without PLAY: LOG IN clicked {Clicks} time(s); last frame LOG IN {Login}, PLAY {Play}, READY {Ready}, AUTO-PLAY {Auto}, launcher window {Window}. Saving a screenshot.",
                    loginClickAttempts,
                    snapshot.LoginPoint is { } lp2 ? $"at ({lp2.X}, {lp2.Y})" : "not found",
                    snapshot.PlayPoint is { } pp2 ? $"at ({pp2.X}, {pp2.Y})" : "not found",
                    snapshot.OcrReadyVisible ? "shown" : "not shown",
                    snapshot.OcrAutoPlayVisible ? "shown" : "not shown",
                    IsLauncherProcessWindowActive() ? "open" : "closed");
                await SaveDiagnosticFrameAsync("launcher-waiting", cancellationToken).ConfigureAwait(false);
            }
        }

        if (triggeredPlayPoint is { } clickPlay)
        {
            _logger.LogInformation("Launcher PLAY button detected at ({X}, {Y}); launching GW2.", clickPlay.X, clickPlay.Y);
            await _input.ClickAsync(clickPlay.X, clickPlay.Y, cancellationToken).ConfigureAwait(false);
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            if (!IsGame3DWindowActive())
            {
                await _input.KeyPressAsync("enter", cancellationToken).ConfigureAwait(false); // the launcher only, never the game
            }

            advanced = true;
            hadLauncherActivity = true;
        }

        if (hadLauncherActivity || IsGame3DWindowActive())
        {
            var launchWaitMs = _requireTemplateFilesOnDisk
                ? Math.Max(Math.Max(_uiWaitMs * 4, _settings.WindowWaitTimeoutSeconds * 1000), 35_000)
                : Math.Max(_uiWaitMs * 2, 150);
            var launchDeadline = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(launchWaitMs);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsPaused) return advanced;

                await EnsureGameWindowAsync(cancellationToken).ConfigureAwait(false);
                frame =await _screenshots.CaptureAsync(CaptureRegion.FullScreen1024x768, cancellationToken).ConfigureAwait(false);

                if (DetectLauncherWhiteCard(frame) is null)
                {
                    if (await TryFindInFrameAsync(frame, "ingame_1.png", TopHeaderRegion, 0.72m, cancellationToken).ConfigureAwait(false) is not null ||
                        await IsTradingPostOpenAndActiveAsync(cancellationToken).ConfigureAwait(false))
                    {
                        break;
                    }

                    if (await FindCharacterSelectPlayAsync(frame, cancellationToken).ConfigureAwait(false) is not null)
                    {
                        break;
                    }
                }

                if (DateTimeOffset.UtcNow >= launchDeadline)
                {
                    break;
                }

                await Task.Delay(Math.Max(_pollMs, 150), cancellationToken).ConfigureAwait(false);
            }
        }

        var charPlay = await FindCharacterSelectPlayAsync(frame, cancellationToken).ConfigureAwait(false);
        if (charPlay is not null)
        {
            // Character Select lists the last played character first. On a fresh launch, a game restart or a
            // disconnect, that is the character the previous run ended on (possibly the one that could not open the
            // TP), so with several parked characters enter a different one, using the same rule as the AFK swap.
            // A swap already chose its slot; otherwise prefer the parked character holding the most unsold stock
            // (never the one the game just left, which may be the one that failed).
            if (!await WaitForCharacterSelectReadyAsync(cancellationToken).ConfigureAwait(false))
            {
                return await EnteredWithoutChoosingAsync(cancellationToken).ConfigureAwait(false);
            }

            int? chosen = _pendingSlot is { } pending && _utcNow() - pending.At < TimeSpan.FromMinutes(3)
                ? pending.Offset
                : null;
            _pendingSlot = null;
            var byStock = false;
            if (chosen is null && _settings.ParkedCharacterCount > 1 &&
                PreferredCharacterNames.Where(name => !CharacterStock.NamesMatch(name, CurrentCharacterName)).ToArray() is { Length: > 0 } preferred)
            {
                chosen = (await FindPortraitByNameAsync(preferred, _settings.ParkedCharacterCount, cancellationToken).ConfigureAwait(false))?.Offset;
                byStock = chosen is not null;
            }

            if (!await IsCharacterSelectShownAsync(cancellationToken).ConfigureAwait(false))
            {
                return await EnteredWithoutChoosingAsync(cancellationToken).ConfigureAwait(false);
            }

            var slotOffset = chosen ?? PickParkedSlotOffset(_settings.RandomizeCharacterSelection, _settings.ParkedCharacterCount);
            var portraitX = DeterministicCharSlotOnePoint.X + (slotOffset * CharacterSlotStrideX);
            var portraitY = DeterministicCharSlotOnePoint.Y;
            var previousName = slotOffset > 0 && !byStock ? await ReadSelectedCharacterNameAsync(cancellationToken).ConfigureAwait(false) : null;
            _logger.LogInformation(
                "Character Select detected; entering parked slot #{Slot} via Play at ({X}, {Y}){Detail}.",
                slotOffset + 1,
                CenterX(charPlay),
                CenterY(charPlay),
                byStock ? " (it holds the most unsold stock)"
                : slotOffset > 0 ? $" instead of the last played character '{previousName ?? "?"}'" : string.Empty);
            if (_requireTemplateFilesOnDisk || slotOffset > 0)
            {
                // Select the portrait first (also focuses it on slow fades) before clicking Play.
                await _input.ClickAsync(portraitX, portraitY, cancellationToken).ConfigureAwait(false);
                await SettleShortAsync(cancellationToken).ConfigureAwait(false);
            }

            CurrentCharacterLabel = $"slot #{slotOffset + 1}";
            CurrentCharacterName = null;
            if (slotOffset > 0 && await ReadSelectedCharacterNameAsync(cancellationToken).ConfigureAwait(false) is { } selectedName)
            {
                CurrentCharacterLabel = $"'{selectedName}' (slot #{slotOffset + 1})";
                CurrentCharacterName = selectedName;
                _logger.LogInformation("Character Select now shows '{Name}' (slot #{Slot}).", selectedName, slotOffset + 1);
            }

            await EnterCharacterSlotAsync(
                charPlay,
                portraitX,
                portraitY,
                Math.Min(1000, Math.Max(100, _uiWaitMs / 2)),
                cancellationToken).ConfigureAwait(false);
            advanced = true;
        }

        return advanced;
    }

    /// <summary>Character Select closed before a portrait was chosen: the game entered a character on its own.</summary>
    private async Task<bool> EnteredWithoutChoosingAsync(CancellationToken cancellationToken)
    {
        _logger.LogWarning("Character Select closed before a character was chosen; continuing with whichever character is entered.");
        CharacterEntries++;
        CurrentCharacterLabel = "the last played character";
        CurrentCharacterName = null;
        await WaitForInWorldAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<LauncherFrameSnapshot> InspectLauncherFrameAsync(
        AutomationFrame frame,
        CancellationToken cancellationToken)
    {
        var whiteCard = DetectLauncherWhiteCard(frame);
        var loginTemplate = whiteCard is null
            ? await TryFindInFrameAsync(frame, "login_launcher.png", LauncherLoginRegion, 0.72m, cancellationToken).ConfigureAwait(false)
            : null;
        var playTemplate = await TryFindInFrameAsync(frame, "play.png", LauncherPlayRegion, 0.72m, cancellationToken).ConfigureAwait(false);

        (int X, int Y)? ocrLogin = null;
        (int X, int Y)? ocrPlay = null;
        var ocrReadyVisible = false;
        var ocrAutoPlayVisible = false;

        if (_textLocator is not null && frame.Pixels.Length > 0)
        {
            var loginLines = await _textLocator.LocateLinesAsync(frame, LauncherLoginOcrRegion, cancellationToken).ConfigureAwait(false);
            foreach (var box in loginLines)
            {
                if (IsOcrLoginLabel(box.Text) && box.Y >= 460 && box.X <= 240)
                {
                    ocrLogin = OcrBoxCenter(box);
                    break;
                }
            }

            // The ArenaNet login form always has LOG IN right under the password box. When the form is on screen
            // (its PASSWORD label read in place) but the button label is not, click the button's measured centre:
            // OCR misread the idle button on 22 Nov and auto-login waited for a hand on the mouse.
            if (ocrLogin is null && !_settings.IsSteamPlatform &&
                loginLines.Any(box => box.Text.Trim().Equals("PASSWORD", StringComparison.OrdinalIgnoreCase) && box.Y is >= 440 and <= 490 && box.X <= 120))
            {
                ocrLogin = LauncherLoginButtonPoint;
            }

            var playLines = await _textLocator.LocateLinesAsync(frame, LauncherPlayOcrRegion, cancellationToken).ConfigureAwait(false);
            foreach (var box in playLines)
            {
                var upper = box.Text.Trim().ToUpperInvariant();
                if (upper.Contains("READY", StringComparison.Ordinal))
                {
                    ocrReadyVisible = true;
                }

                if (upper.Contains("AUTO-PLAY", StringComparison.Ordinal) || upper.Contains("AUTOPLAY", StringComparison.Ordinal))
                {
                    ocrAutoPlayVisible = true;
                }

                if (ocrPlay is null && IsOcrPlayButtonLabel(upper) && box.X >= 600)
                {
                    ocrPlay = OcrBoxCenter(box);
                }
            }
        }

        // OCR first, then the template, then the white-card pixel geometry.
        var loginPoint = ocrLogin ?? CenterOf(loginTemplate);
        if (loginPoint is null && whiteCard is { LoginButtonActive: true })
        {
            loginPoint = (whiteCard.LoginX, whiteCard.LoginY);
        }

        (int X, int Y)? playPoint = null;
        if (!ocrReadyVisible && (_settings.IsSteamPlatform || loginPoint is null))
        {
            playPoint = ocrPlay ?? CenterOf(playTemplate);
            if (playPoint is null && whiteCard is { PlayButtonActive: true })
            {
                playPoint = (whiteCard.PlayX, whiteCard.PlayY);
            }
        }

        return new LauncherFrameSnapshot(
            WhiteCard: whiteCard,
            LoginPoint: loginPoint,
            PlayPoint: playPoint,
            OcrReadyVisible: ocrReadyVisible,
            OcrAutoPlayVisible: ocrAutoPlayVisible);
    }

    private static (int X, int Y)? CenterOf(TemplateMatch? match) =>
        match is null ? null : (CenterX(match), CenterY(match));

    private async Task<TemplateMatch?> FindCharacterSelectPlayAsync(
        AutomationFrame frame,
        CancellationToken cancellationToken)
    {
        // Only the bottom-right corner, where Play really is (792, 735). A whole-screen search also matched a red
        // dialog button mid-screen at (640, 456): on 21-22 Nov 6 of 7 character swaps clicked it plus Enter, which
        // logged the account out to the launcher, relaunched, and entered whichever character the relaunch picked.
        var charPlay = await TryFindInFrameAsync(frame, "charscreen_play.png", CharSelectPlayRegion, 0.72m, cancellationToken).ConfigureAwait(false);
        if (charPlay is not null)
        {
            return charPlay;
        }

        if (_textLocator is null || frame.Pixels.Length == 0)
        {
            return null;
        }

        var bottomLines = await _textLocator.LocateLinesAsync(frame, CharSelectBottomBarRegion, cancellationToken).ConfigureAwait(false);
        if (bottomLines.Count == 0)
        {
            return null;
        }

        var hasCharSelectContext = bottomLines.Any(line =>
            line.Text.Contains("Log Out", StringComparison.OrdinalIgnoreCase) ||
            line.Text.Contains("Character Slots", StringComparison.OrdinalIgnoreCase) ||
            line.Text.Contains("Region Selection", StringComparison.OrdinalIgnoreCase) ||
            line.Text.Contains("Create", StringComparison.OrdinalIgnoreCase));
        if (!hasCharSelectContext)
        {
            return null;
        }

        var playLines = await _textLocator.LocateLinesAsync(frame, CharSelectPlayRegion, cancellationToken).ConfigureAwait(false);
        var playBox = playLines.FirstOrDefault(line =>
            string.Equals(line.Text.Trim(), "Play", StringComparison.OrdinalIgnoreCase) ||
            line.Text.Contains("Play", StringComparison.OrdinalIgnoreCase));
        return playBox is not null
            ? new TemplateMatch(playBox.X, playBox.Y, playBox.Width, playBox.Height, 0.90m)
            : null;
    }

    /// <summary>
    /// The launcher's LOG IN label. With the "Visions of Eternity" theme (22 Nov) OCR reads the idle button as
    /// "LOG N": the thin I is lost on the dark orange. It was only read whole while the mouse hovered the button, so
    /// auto-login waited until someone moved the mouse there. O may read as 0 and I as 1, l or |.
    /// </summary>
    internal static bool IsOcrLoginLabel(string text)
    {
        var normalized = text.Trim().ToUpperInvariant().Replace(" ", string.Empty);
        return System.Text.RegularExpressions.Regex.IsMatch(normalized, "^L[O0]G[I1L|]?N$");
    }

    private static bool IsOcrPlayButtonLabel(string upperText)
    {
        var cleaned = upperText.Trim();
        if (cleaned.Contains("PLAYABLE", StringComparison.Ordinal) ||
            cleaned.Contains("AUTO-PLAY", StringComparison.Ordinal) ||
            cleaned.Contains("AUTOPLAY", StringComparison.Ordinal) ||
            cleaned.Contains("READY", StringComparison.Ordinal))
        {
            return false;
        }

        return cleaned == "PLAY" || cleaned.StartsWith("PLAY ", StringComparison.Ordinal) || cleaned.EndsWith(" PLAY", StringComparison.Ordinal);
    }

    private async Task<bool> DismissLauncherDialogsAsync(AutomationFrame frame, CancellationToken cancellationToken)
    {
        var dismissed = false;
        foreach (var dialogButton in new[] { "accept_agreement.png", "remind_me_later.png" })
        {
            var button = await TryFindInFrameAsync(frame, dialogButton, CaptureRegion.FullScreen1024x768, 0.78m, cancellationToken).ConfigureAwait(false);
            if (button is not null)
            {
                await ClickCenterAndSettleAsync(button, cancellationToken).ConfigureAwait(false);
                dismissed = true;
            }
        }

        return dismissed;
    }

    private bool IsLauncherProcessWindowActive() => InspectGameWindow() is { IsLauncherWindow: true };

    private bool IsGame3DWindowActive() => InspectGameWindow() is { IsGame3DWindow: true };

    /// <summary>The live GW2 window's class/title inspection; null in tests (no template files) or when not found.</summary>
    private Gw2WindowInspection? InspectGameWindow() =>
        _requireTemplateFilesOnDisk && _windows is Win32WindowController windowController
            ? windowController.InspectWindow(Gw2ClientSettings.DefaultWindowName)
            : null;

    private async Task<bool> IsLauncherStillActiveAsync(CancellationToken cancellationToken)
    {
        if (IsLauncherProcessWindowActive())
        {
            return true;
        }

        var frame = await _screenshots.CaptureAsync(CaptureRegion.FullScreen1024x768, cancellationToken).ConfigureAwait(false);
        return DetectLauncherWhiteCard(frame) is not null;
    }
}
