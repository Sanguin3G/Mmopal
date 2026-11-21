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
        var width = _settings.WindowWidth > 0 ? _settings.WindowWidth : 1024;
        var height = _settings.WindowHeight > 0 ? _settings.WindowHeight : 768;
        await _windows.EnsureWindowAsync(Gw2ClientSettings.DefaultWindowName, width, height, cancellationToken).ConfigureAwait(false);
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
                            await _windows.EnsureWindowAsync(Gw2ClientSettings.DefaultWindowName, width, height, cancellationToken).ConfigureAwait(false);
                        }

                        // Re-check on a fresh frame right before clicking: the launcher may have moved on to the
                        // news page while the previous frame was being inspected.
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
                await _windows.EnsureWindowAsync(Gw2ClientSettings.DefaultWindowName, width, height, cancellationToken).ConfigureAwait(false);
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
        }

        if (triggeredPlayPoint is { } clickPlay)
        {
            _logger.LogInformation("Launcher PLAY button detected at ({X}, {Y}); launching GW2.", clickPlay.X, clickPlay.Y);
            await _input.ClickAsync(clickPlay.X, clickPlay.Y, cancellationToken).ConfigureAwait(false);
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            await _input.KeyPressAsync("enter", cancellationToken).ConfigureAwait(false);
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

                await _windows.EnsureWindowAsync(Gw2ClientSettings.DefaultWindowName, width, height, cancellationToken).ConfigureAwait(false);
                frame = await _screenshots.CaptureAsync(CaptureRegion.FullScreen1024x768, cancellationToken).ConfigureAwait(false);

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
            var slotOffset = _pendingSlot is { } pending && _utcNow() - pending.At < TimeSpan.FromMinutes(3)
                ? pending.Offset
                : PickParkedSlotOffset(_settings.RandomizeCharacterSelection, _settings.ParkedCharacterCount);
            _pendingSlot = null;
            var portraitX = DeterministicCharSlotOnePoint.X + (slotOffset * CharacterSlotStrideX);
            var portraitY = DeterministicCharSlotOnePoint.Y;
            var previousName = slotOffset > 0 ? await ReadSelectedCharacterNameAsync(cancellationToken).ConfigureAwait(false) : null;
            _logger.LogInformation(
                "Character Select detected; entering parked slot #{Slot} via Play at ({X}, {Y}){Detail}.",
                slotOffset + 1,
                CenterX(charPlay),
                CenterY(charPlay),
                slotOffset > 0 ? $" instead of the last played character '{previousName ?? "?"}'" : string.Empty);
            if (_requireTemplateFilesOnDisk || slotOffset > 0)
            {
                // Select the portrait first (also focuses it on slow fades) before clicking Play.
                await _input.ClickAsync(portraitX, portraitY, cancellationToken).ConfigureAwait(false);
                await SettleShortAsync(cancellationToken).ConfigureAwait(false);
            }

            CurrentCharacterLabel = $"slot #{slotOffset + 1}";
            if (slotOffset > 0 && await ReadSelectedCharacterNameAsync(cancellationToken).ConfigureAwait(false) is { } selectedName)
            {
                CurrentCharacterLabel = $"'{selectedName}' (slot #{slotOffset + 1})";
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
                    ocrLogin = (box.X + Math.Max(1, box.Width / 2), box.Y + Math.Max(1, box.Height / 2));
                    break;
                }
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
                    ocrPlay = (box.X + Math.Max(1, box.Width / 2), box.Y + Math.Max(1, box.Height / 2));
                }
            }
        }

        var loginPoint = ocrLogin;
        if (loginPoint is null && loginTemplate is not null)
        {
            loginPoint = (CenterX(loginTemplate), CenterY(loginTemplate));
        }

        if (loginPoint is null && whiteCard is { LoginButtonActive: true })
        {
            loginPoint = (whiteCard.LoginX, whiteCard.LoginY);
        }

        (int X, int Y)? playPoint = null;
        if (!ocrReadyVisible && (_settings.IsSteamPlatform || loginPoint is null))
        {
            playPoint = ocrPlay;
            if (playPoint is null && playTemplate is not null)
            {
                playPoint = (CenterX(playTemplate), CenterY(playTemplate));
            }

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

    private async Task<TemplateMatch?> FindCharacterSelectPlayAsync(
        AutomationFrame frame,
        CancellationToken cancellationToken)
    {
        var charPlay = await TryFindInFrameAsync(frame, "charscreen_play.png", CharSelectPlayRegion, 0.72m, cancellationToken).ConfigureAwait(false)
                       ?? await TryFindInFrameAsync(frame, "charscreen_play.png", CaptureRegion.FullScreen1024x768, 0.75m, cancellationToken).ConfigureAwait(false);
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

    private static bool IsOcrLoginLabel(string text)
    {
        var normalized = text.Trim().ToUpperInvariant().Replace(" ", string.Empty);
        return normalized is "LOGIN" or "LOG IN";
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
        var agreement = await TryFindInFrameAsync(frame, "accept_agreement.png", CaptureRegion.FullScreen1024x768, 0.78m, cancellationToken).ConfigureAwait(false);
        if (agreement is not null)
        {
            await ClickCenterAsync(agreement, cancellationToken).ConfigureAwait(false);
            await SettleAsync(cancellationToken).ConfigureAwait(false);
            dismissed = true;
        }

        var remindLater = await TryFindInFrameAsync(frame, "remind_me_later.png", CaptureRegion.FullScreen1024x768, 0.78m, cancellationToken).ConfigureAwait(false);
        if (remindLater is not null)
        {
            await ClickCenterAsync(remindLater, cancellationToken).ConfigureAwait(false);
            await SettleAsync(cancellationToken).ConfigureAwait(false);
            dismissed = true;
        }

        return dismissed;
    }

    private bool IsLauncherProcessWindowActive() =>
        _requireTemplateFilesOnDisk &&
        _windows is Win32WindowController winCtrl &&
        winCtrl.InspectWindow(Gw2ClientSettings.DefaultWindowName) is { IsLauncherWindow: true };

    private bool IsGame3DWindowActive() =>
        _requireTemplateFilesOnDisk &&
        _windows is Win32WindowController winCtrl &&
        winCtrl.InspectWindow(Gw2ClientSettings.DefaultWindowName) is { IsGame3DWindow: true };

    internal static LauncherWhiteCardState? DetectLauncherWhiteCard(AutomationFrame frame)
    {
        if (frame.Pixels.Length == 0 || frame.Region.Width < 500 || frame.Region.Height < 500)
        {
            return null;
        }

        var width = frame.Region.Width;
        var height = frame.Region.Height;
        var stride = frame.Stride > 0 ? frame.Stride : width * 4;
        if (frame.Pixels.Length < stride * height)
        {
            return null;
        }

        var whiteSamples = 0;
        var totalSamples = 0;
        var leftSampleLimit = Math.Min(460, width);
        var bottomSampleLimit = Math.Min(720, height);
        for (var y = 180; y < bottomSampleLimit; y += 4)
        {
            var rowOffset = y * stride;
            for (var x = 20; x < leftSampleLimit; x += 4)
            {
                totalSamples++;
                var px = rowOffset + (x * 4);
                var b = frame.Pixels[px];
                var g = frame.Pixels[px + 1];
                var r = frame.Pixels[px + 2];
                if (r > 215 && g > 210 && b > 195)
                {
                    whiteSamples++;
                }
            }
        }

        if (totalSamples == 0 || whiteSamples * 100 / totalSamples < 35)
        {
            return null;
        }

        // Measure vertical bounds along the clean left card margin so bright right-side theme artwork
        // (such as EoD Jade Mech highlights) cannot shift cardTop upward.
        var cardTop = int.MaxValue;
        var cardBottom = 0;
        var marginMaxX = Math.Min(44, width - 1);
        for (var y = 20; y < height; y += 2)
        {
            var rowOffset = y * stride;
            var marginWhite = 0;
            var marginTotal = 0;
            for (var x = 20; x <= marginMaxX; x += 4)
            {
                marginTotal++;
                var px = rowOffset + (x * 4);
                if (frame.Pixels[px + 2] > 215 && frame.Pixels[px + 1] > 210 && frame.Pixels[px] > 195)
                {
                    marginWhite++;
                }
            }

            if (marginTotal > 0 && marginWhite * 100 / marginTotal >= 60)
            {
                if (y < cardTop) cardTop = y;
                if (y > cardBottom) cardBottom = y;
            }
        }

        if (cardBottom - cardTop < 250)
        {
            cardTop = int.MaxValue;
            cardBottom = 0;
            var bandMaxX = Math.Min(420, width);
            for (var y = 100; y < Math.Min(height - 2, 755); y += 3)
            {
                var rowOffset = y * stride;
                for (var x = 10; x < bandMaxX; x += 5)
                {
                    var px = rowOffset + (x * 4);
                    if (frame.Pixels[px + 2] > 215 && frame.Pixels[px + 1] > 210 && frame.Pixels[px] > 195)
                    {
                        if (y < cardTop) cardTop = y;
                        if (y > cardBottom) cardBottom = y;
                    }
                }
            }
        }

        var cardLeft = int.MaxValue;
        var cardRight = 0;
        var maxScanX = width - 4;
        for (var y = Math.Max(0, cardTop); y <= Math.Min(height - 2, cardBottom); y += 6)
        {
            var rowOffset = y * stride;
            for (var x = 10; x < maxScanX; x += 5)
            {
                var px = rowOffset + (x * 4);
                if (frame.Pixels[px + 2] > 215 && frame.Pixels[px + 1] > 210 && frame.Pixels[px] > 195)
                {
                    if (x < cardLeft) cardLeft = x;
                    if (x > cardRight) cardRight = x;
                }
            }
        }

        if (cardRight - cardLeft < 400 || cardBottom - cardTop < 250)
        {
            return null;
        }

        var rawCardW = cardRight - cardLeft;
        var rawCardH = cardBottom - cardTop;
        // Themes with dark bottom/right art (Path of Fire, Living World Season 4, Icebrood Saga) truncate
        // white-pixel bounds even though the HTML form stays anchored at fixed offsets from (cardLeft, cardTop).
        var cardW = cardLeft <= 40 && rawCardW < 960
            ? Math.Max(rawCardW, Math.Min(width - 20 - cardLeft, 965))
            : rawCardW;
        var cardH = cardTop is >= 140 and <= 230 && rawCardH < 530
            ? Math.Max(rawCardH, Math.Min(height - 20 - cardTop, 532))
            : rawCardH;
        var loginX = Math.Clamp(cardLeft + (int)Math.Round(cardW * 0.098), 0, width - 1);
        var loginY = Math.Clamp(cardTop + (int)Math.Round(cardH * 0.665), 0, height - 1);
        var playX = Math.Clamp(cardLeft + (int)Math.Round(cardW * 0.780), 0, width - 1);
        var playY = Math.Clamp(cardTop + (int)Math.Round(cardH * 0.858), 0, height - 1);

        var buttonColored = 0;
        var buttonTotal = 0;
        var btnMinX = Math.Max(0, loginX - 40);
        var btnMaxX = Math.Min(width - 1, loginX + 40);
        var btnMinY = Math.Max(0, loginY - 10);
        var btnMaxY = Math.Min(height - 1, loginY + 10);
        for (var y = btnMinY; y <= btnMaxY; y += 2)
        {
            for (var x = btnMinX; x <= btnMaxX; x += 4)
            {
                buttonTotal++;
                var (r, g, b) = SampleRgb(frame.Pixels, stride, x, y);
                if (Math.Min(r, Math.Min(g, b)) < 195)
                {
                    buttonColored++;
                }
            }
        }

        var surroundWhite = 0;
        var surroundTotal = 0;
        foreach (var sy in new[] { loginY - 26, loginY + 26 })
        {
            if (sy < 0 || sy >= height) continue;
            for (var x = btnMinX; x <= btnMaxX; x += 4)
            {
                surroundTotal++;
                var (r, g, b) = SampleRgb(frame.Pixels, stride, x, sy);
                if (IsLightCardPixel(r, g, b))
                {
                    surroundWhite++;
                }
            }
        }

        var loginActive = buttonTotal > 0 &&
                          buttonColored * 100 / buttonTotal >= 35 &&
                          (surroundTotal == 0 || surroundWhite * 100 / surroundTotal >= 55);

        if (!loginActive)
        {
            // Scan left login column vertically for a compact colored/dark LOG IN box bounded by light card pixels
            // when expansion art or unclipped launcher height shifts the button Y slightly.
            var colMinX = Math.Clamp(cardLeft + 30, 0, width - 1);
            var colMaxX = Math.Clamp(cardLeft + 215, colMinX + 1, width - 1);
            var colMinY = Math.Clamp(cardTop + (int)Math.Round(cardH * 0.50), 20, height - 20);
            var colMaxY = Math.Clamp(cardTop + (int)Math.Round(cardH * 0.78), colMinY, height - 20);
            var bandStart = -1;
            var bestBandStart = -1;
            var bestBandEnd = -1;
            var bestBandPixels = 0;

            for (var y = colMinY; y <= colMaxY; y += 2)
            {
                var rowDark = 0;
                var rowTotal = 0;
                for (var x = colMinX; x <= colMaxX; x += 4)
                {
                    rowTotal++;
                    var (r, g, b) = SampleRgb(frame.Pixels, stride, x, y);
                    if (Math.Min(r, Math.Min(g, b)) < 185)
                    {
                        rowDark++;
                    }
                }

                var isButtonRow = rowTotal > 0 && rowDark * 100 / rowTotal >= 45;
                if (isButtonRow)
                {
                    if (bandStart < 0) bandStart = y;
                    if (rowDark > bestBandPixels)
                    {
                        bestBandPixels = rowDark;
                        bestBandStart = bandStart;
                        bestBandEnd = y;
                    }
                }
                else
                {
                    if (bandStart >= 0 && bestBandStart == bandStart && y - 2 - bandStart >= 14 && y - 2 - bandStart <= 46)
                    {
                        bestBandEnd = y - 2;
                        break;
                    }

                    bandStart = -1;
                }
            }

            if (bestBandStart >= 0 && bestBandEnd - bestBandStart is >= 14 and <= 46)
            {
                var aboveY = Math.Max(0, bestBandStart - 10);
                var belowY = Math.Min(height - 1, bestBandEnd + 14);
                var lightAround = 0;
                var totalAround = 0;
                foreach (var sy in new[] { aboveY, belowY })
                {
                    for (var x = colMinX; x <= colMaxX; x += 6)
                    {
                        totalAround++;
                        var (r, g, b) = SampleRgb(frame.Pixels, stride, x, sy);
                        if (IsLightCardPixel(r, g, b))
                        {
                            lightAround++;
                        }
                    }
                }

                if (totalAround > 0 && lightAround * 100 / totalAround >= 55)
                {
                    var sumX = 0L;
                    var sumY = 0L;
                    var hits = 0;
                    for (var y = bestBandStart; y <= bestBandEnd; y += 2)
                    {
                        for (var x = colMinX; x <= colMaxX; x += 4)
                        {
                            var (r, g, b) = SampleRgb(frame.Pixels, stride, x, y);
                            if (Math.Min(r, Math.Min(g, b)) < 185)
                            {
                                sumX += x;
                                sumY += y;
                                hits++;
                            }
                        }
                    }

                    if (hits > 0)
                    {
                        loginX = (int)(sumX / hits);
                        loginY = (int)(sumY / hits);
                        loginActive = true;
                    }
                }
            }
        }

        var playColored = 0;
        var playTotal = 0;
        var playMinX = Math.Max(0, playX - 30);
        var playMaxX = Math.Min(width - 1, playX + 30);
        var playMinY = Math.Max(0, playY - 10);
        var playMaxY = Math.Min(height - 1, playY + 10);
        for (var y = playMinY; y <= playMaxY; y += 2)
        {
            for (var x = playMinX; x <= playMaxX; x += 5)
            {
                playTotal++;
                var (pr, pg, pb) = SampleRgb(frame.Pixels, stride, x, y);
                if (IsColoredPlayPixel(pr, pg, pb))
                {
                    playColored++;
                }
            }
        }

        var playActive = !loginActive && playTotal > 0 && playColored * 100 / playTotal >= 25;
        if (!loginActive && !playActive)
        {
            // Expand slightly around predicted PLAY coordinates to locate the real PLAY button centroid across launcher sizes.
            var searchMinX = Math.Max(620, playX - 90);
            var searchMaxX = Math.Min(width - 10, playX + 110);
            var searchMinY = Math.Max(580, playY - 25);
            var searchMaxY = Math.Min(height - 10, playY + 60);
            var sumX = 0L;
            var sumY = 0L;
            var count = 0;
            var scanned = 0;
            for (var y = searchMinY; y <= searchMaxY; y += 2)
            {
                for (var x = searchMinX; x <= searchMaxX; x += 4)
                {
                    scanned++;
                    var (pr, pg, pb) = SampleRgb(frame.Pixels, stride, x, y);
                    if (IsColoredPlayPixel(pr, pg, pb))
                    {
                        sumX += x;
                        sumY += y;
                        count++;
                    }
                }
            }

            if (scanned > 0 && count >= 12 && count * 100 / scanned >= 10)
            {
                playX = (int)(sumX / count);
                playY = (int)(sumY / count);
                playActive = true;
            }
        }

        return new LauncherWhiteCardState(loginX, loginY, playX, playY, loginActive, playActive);
    }

    private static bool IsLightCardPixel(byte r, byte g, byte b) =>
        r > 200 && g > 160 && b > 145 && Math.Min(r, Math.Min(g, b)) >= 145;

    private static bool IsColoredPlayPixel(byte r, byte g, byte b)
    {
        var min = Math.Min(r, Math.Min(g, b));
        var bright = Math.Max(r, Math.Max(g, b));
        return bright > 110 && (bright - min) >= 35 && min < 175;
    }

    private static (byte R, byte G, byte B) SampleRgb(byte[] pixels, int stride, int x, int y)
    {
        var idx = y * stride + (x * 4);
        return (pixels[idx + 2], pixels[idx + 1], pixels[idx]);
    }

    private async Task<bool> IsLauncherStillActiveAsync(CancellationToken cancellationToken)
    {
        if (IsLauncherProcessWindowActive())
        {
            return true;
        }

        var frame = await _screenshots.CaptureAsync(CaptureRegion.FullScreen1024x768, cancellationToken).ConfigureAwait(false);
        return DetectLauncherWhiteCard(frame) is not null;
    }

    internal sealed record LauncherWhiteCardState(
        int LoginX,
        int LoginY,
        int PlayX,
        int PlayY,
        bool LoginButtonActive,
        bool PlayButtonActive);

    private sealed record LauncherFrameSnapshot(
        LauncherWhiteCardState? WhiteCard,
        (int X, int Y)? LoginPoint,
        (int X, int Y)? PlayPoint,
        bool OcrReadyVisible,
        bool OcrAutoPlayVisible)
    {
        public bool HasAnySignal =>
            WhiteCard is not null ||
            LoginPoint is not null ||
            PlayPoint is not null ||
            OcrReadyVisible ||
            OcrAutoPlayVisible;
    }
}
