using Microsoft.Extensions.Logging;
using Mmopal.Contracts;
using Mmopal.Gw2;
using Mmopal.Native;

namespace Mmopal.Worker;

/// <summary>
/// High-ping-aware, bounded UI driver for the Guild Wars 2 Launcher, Character Select, and
/// Trading Post (1024x768). Uses disjoint tab regions, cog-relative search focusing,
/// filtered flip-only selling, and theme-independent launcher geometry.
/// </summary>
public sealed class Gw2TradingPostUiDriver
{
    private const int MaxLoginClickAttempts = 3;
    private const int MaxSearchRowsToTry = 3;
    // Measured on live 1024x768 screenshots: results header "Item" at y~227, result rows 58 px apart.
    private const int SearchResultRowStride = 58;
    private static readonly CaptureRegion SearchResultsHeaderRegion = new(320, 195, 470, 60);
    // Centered in-game message box text (above its OK button).
    private static readonly CaptureRegion PopupTextRegion = new(340, 305, 400, 120);
    // Right-hand result list (Buy/Sell Items rows, My Transactions rows); excludes the category panel on the left.
    private static readonly CaptureRegion SearchResultsListRegion = new(325, 240, 450, 300);
    // Buy dialog: item name title at ~(347,175) and the Max. Price gold/silver/copper boxes at y~302-322.
    private static readonly CaptureRegion ItemDialogTitleRegion = new(330, 165, 500, 45);
    private static readonly CaptureRegion ItemDialogMaxPriceRegion = new(380, 294, 250, 34);
    internal static readonly TimeSpan MinOrderAgeBeforeOutbidCancel = TimeSpan.FromMinutes(20);
    private static readonly CaptureRegion TopHeaderRegion = new(0, 0, 1024, 400);
    private static readonly CaptureRegion LeftSidebarTpTabRegion = new(0, 240, 80, 140);
    private static readonly CaptureRegion BuyTabRegion = new(475, 90, 175, 150);
    private static readonly CaptureRegion SellTabRegion = new(645, 90, 170, 150);
    private static readonly CaptureRegion TransactionsTabRegion = new(810, 90, 175, 150);
    private static readonly CaptureRegion SearchFieldRegion = new(0, 180, 330, 140);
    private static readonly CaptureRegion LauncherLoginRegion = new(20, 440, 280, 220);
    private static readonly CaptureRegion LauncherLoginOcrRegion = new(10, 340, 360, 360);
    private static readonly CaptureRegion LauncherPlayRegion = new(600, 560, 424, 208);
    private static readonly CaptureRegion LauncherPlayOcrRegion = new(540, 510, 484, 258);
    private static readonly CaptureRegion CharSelectPlayRegion = new(680, 680, 220, 88);
    private static readonly CaptureRegion CharSelectBottomBarRegion = new(0, 680, 1024, 88);
    // Large name of the selected character on the left of Character Select (e.g. "Mesmerising Rizzo" at ~57,400).
    private static readonly CaptureRegion CharSelectNameRegion = new(40, 375, 440, 50);
    private static readonly CaptureRegion InGameInteractRegion = new(250, 220, 550, 500);
    private static readonly CaptureRegion ModalPriceSpinnerRegion = new(545, 265, 80, 65);
    private static readonly CaptureRegion ModalCloseButtonRegion = new(720, 120, 90, 80);
    private static readonly CaptureRegion BlackLionCrestRegion = new(0, 0, 120, 120);

    private static readonly (int X, int Y) DeterministicLeftSidebarTpPoint = (32, 315);
    private static readonly (int X, int Y) DeterministicBuyTabPoint = (562, 165);
    private static readonly (int X, int Y) DeterministicSellTabPoint = (725, 165);
    private static readonly (int X, int Y) DeterministicSearchBoxPoint = (255, 258);
    private static readonly (int X, int Y) DeterministicRestPoint = (115, 65);
    private static readonly (int X, int Y) DeterministicCharSlotOnePoint = (258, 640);
    private const int CharacterSlotStrideX = 122;

    private readonly IScreenshotProvider _screenshots;
    private readonly ITemplateMatcher _templates;
    private readonly IInputController _input;
    private readonly IWindowController _windows;
    private readonly IUiTextLocator? _textLocator;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly string _templateRoot;
    private readonly Gw2ClientSettings _settings;
    private readonly ILogger _logger;
    private readonly int _uiWaitMs;
    private readonly int _settleMs;
    private readonly int _pollMs;
    private readonly bool _requireTemplateFilesOnDisk;
    private readonly Func<bool>? _isPaused;

    public Gw2TradingPostUiDriver(
        IScreenshotProvider screenshots,
        ITemplateMatcher templates,
        IInputController input,
        IWindowController windows,
        string templateRoot,
        Gw2ClientSettings settings,
        ILogger logger,
        int pollIntervalMs = 150,
        bool requireTemplateFilesOnDisk = true,
        Func<bool>? isPaused = null,
        IUiTextLocator? textLocator = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _screenshots = screenshots ?? throw new ArgumentNullException(nameof(screenshots));
        _templates = templates ?? throw new ArgumentNullException(nameof(templates));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _templateRoot = templateRoot ?? string.Empty;
        _settings = settings ?? new Gw2ClientSettings();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _uiWaitMs = Math.Clamp(_settings.UiLoadWaitMs, 20, 30_000);
        _settleMs = Math.Clamp(_settings.ActionSettleDelayMs, 0, 5_000);
        _pollMs = Math.Max(5, pollIntervalMs);
        _requireTemplateFilesOnDisk = requireTemplateFilesOnDisk;
        _isPaused = isPaused;
        _textLocator = textLocator ?? (requireTemplateFilesOnDisk ? new WindowsOcrReader() : null);
    }

    private bool IsPaused => _isPaused?.Invoke() == true;

    /// <summary>
    /// Picks outbid buy orders that can be canceled safely. The cancel flow searches the item name in
    /// My Transactions and clicks the first Cancel button, so an order is skipped whenever that search would
    /// list more than one of our buy orders (a duplicate order for the same item, or another item whose name
    /// contains this one) — otherwise the click could cancel the wrong order.
    /// Orders younger than <see cref="MinOrderAgeBeforeOutbidCancel"/> are kept: competing bots undercut by a few
    /// copper within minutes, and cancelling on every undercut meant no buy order ever stayed up long enough to fill.
    /// </summary>
    internal static Transaction[] SelectSafeOutbidCancels(
        IReadOnlyList<Transaction> transactions,
        int maxCancelsPerPass,
        DateTimeOffset nowUtc,
        out IReadOnlyList<string> ambiguousNames,
        out int keptYoungCount)
    {
        var buyNames = transactions
            .Where(tx => tx.Type == TransactionType.Buy && !string.IsNullOrWhiteSpace(tx.ItemName))
            .Select(tx => tx.ItemName.Trim())
            .ToArray();
        var skipped = new List<string>();
        var safe = new List<Transaction>();
        keptYoungCount = 0;
        foreach (var tx in transactions)
        {
            if (safe.Count >= maxCancelsPerPass) break;
            if (tx.Type != TransactionType.Buy ||
                string.IsNullOrWhiteSpace(tx.ItemName) ||
                tx.MinimumBuyPriceCopper is not { } topBid ||
                topBid <= tx.PriceCopper)
            {
                continue;
            }

            if (nowUtc - tx.Created < MinOrderAgeBeforeOutbidCancel)
            {
                keptYoungCount++;
                continue;
            }

            var name = tx.ItemName.Trim();
            var searchHits = buyNames.Count(other => other.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (searchHits > 1)
            {
                if (!skipped.Contains(name, StringComparer.OrdinalIgnoreCase)) skipped.Add(name);
                continue;
            }

            safe.Add(tx);
        }

        ambiguousNames = skipped;
        return safe.ToArray();
    }

    /// <summary>
    /// Decides whether the price showing in the buy dialog is safe to submit. The planner priced the order from
    /// the API (<paramref name="expectedCopper"/> = top bid + overbid) but the dialog is filled by clicking the
    /// in-game order book, so a misclick can silently raise the bid (e.g. onto a seller row) or drop it under the top
    /// bid where it never fills. Only a plausibly real reading aborts; unreadable or wildly-off readings are treated
    /// as OCR misses so an uncalibrated reader can never stop all buying.
    /// </summary>
    internal static (bool Abort, string Message) EvaluateVisibleBuyPrice(long expectedCopper, int overbidMaxCopper, long? visibleCopper)
    {
        if (expectedCopper <= 0)
        {
            return (false, "No planned price to compare against; placing without a price check.");
        }

        if (visibleCopper is not { } visible || visible <= 0)
        {
            return (false, $"Visible price unreadable; placing at the in-game price (planned {MarketScanItem.FormatCoins(expectedCopper)}).");
        }

        var ceiling = expectedCopper + Math.Max(0, overbidMaxCopper) + Math.Max(5L, expectedCopper * 3 / 100);
        if (visible > expectedCopper * 3 || visible * 3 < expectedCopper)
        {
            return (false, $"Visible price {MarketScanItem.FormatCoins(visible)} is implausible vs planned {MarketScanItem.FormatCoins(expectedCopper)}; treating it as an OCR misread.");
        }

        if (visible > ceiling)
        {
            return (true, $"visible price {MarketScanItem.FormatCoins(visible)} is above the ceiling {MarketScanItem.FormatCoins(ceiling)} (planned {MarketScanItem.FormatCoins(expectedCopper)}).");
        }

        // A bid this far under the top bid will not fill; it only sits there until Outbid Refresh cancels it.
        var floor = expectedCopper * 9 / 10;
        return visible < floor
            ? (true, $"visible price {MarketScanItem.FormatCoins(visible)} is more than 10% under the planned {MarketScanItem.FormatCoins(expectedCopper)}; the order would sit below the top bid and not fill.")
            : (false, $"Visible price {MarketScanItem.FormatCoins(visible)} OK (planned {MarketScanItem.FormatCoins(expectedCopper)}, ceiling {MarketScanItem.FormatCoins(ceiling)}).");
    }

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
            _logger.LogInformation("Character Select detected; selecting character and entering world via Play at ({X}, {Y}).", CenterX(charPlay), CenterY(charPlay));
            if (_requireTemplateFilesOnDisk)
            {
                // Ensure the first parked character portrait is focused before clicking Play on slow fades.
                await _input.ClickAsync(DeterministicCharSlotOnePoint.X, DeterministicCharSlotOnePoint.Y, cancellationToken).ConfigureAwait(false);
                await SettleShortAsync(cancellationToken).ConfigureAwait(false);
            }

            await EnterCharacterSlotAsync(
                charPlay,
                DeterministicCharSlotOnePoint.X,
                DeterministicCharSlotOnePoint.Y,
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
            await ClickCenterAsync(okButton, cancellationToken).ConfigureAwait(false);
            await SettleAsync(cancellationToken).ConfigureAwait(false);
        }

        var width = _settings.WindowWidth > 0 ? _settings.WindowWidth : 1024;
        var height = _settings.WindowHeight > 0 ? _settings.WindowHeight : 768;
        await _windows.EnsureWindowAsync(Gw2ClientSettings.DefaultWindowName, width, height, cancellationToken).ConfigureAwait(false);

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
                await _input.KeyPressAsync("escape", cancellationToken).ConfigureAwait(false);
                await SettleShortAsync(cancellationToken).ConfigureAwait(false);
                if (await TryFindOnceAsync("logout.png", CaptureRegion.FullScreen1024x768, 0.74m, cancellationToken).ConfigureAwait(false) is not null)
                {
                    await _input.KeyPressAsync("escape", cancellationToken).ConfigureAwait(false);
                    await SettleShortAsync(cancellationToken).ConfigureAwait(false);
                }
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

        var width = _settings.WindowWidth > 0 ? _settings.WindowWidth : 1024;
        var height = _settings.WindowHeight > 0 ? _settings.WindowHeight : 768;
        await _windows.EnsureWindowAsync(Gw2ClientSettings.DefaultWindowName, width, height, cancellationToken).ConfigureAwait(false);

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
            await _windows.EnsureWindowAsync(Gw2ClientSettings.DefaultWindowName, width, height, cancellationToken).ConfigureAwait(false);
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

            await _windows.EnsureWindowAsync(Gw2ClientSettings.DefaultWindowName, width, height, cancellationToken).ConfigureAwait(false);

            var useTpFallback = attempt == 3 && !string.Equals(tpKey, interactKey, StringComparison.OrdinalIgnoreCase);
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

            var found = await WaitForTradingPostOrSidebarAsync(cancellationToken).ConfigureAwait(false);
            if (found)
            {
                if (await IsTradingPostOpenAndActiveAsync(cancellationToken).ConfigureAwait(false) ||
                    await TrySwitchFromBlackLionToTradingPostAsync(cancellationToken).ConfigureAwait(false))
                {
                    return (true, $"Trading Post opened via '{keyToPress}' on attempt {attempt}.");
                }
            }
        }

        return (false, $"Trading Post did not open after 3 bounded attempts using '{interactKey}'/'{tpKey}'. Confirm your character is standing next to a TP NPC.");
    }

    public async Task<bool> CollectDeliveryBoxAsync(CancellationToken cancellationToken = default)
    {
        var takeAll = await TryFindOnceAsync("take_all.png", CaptureRegion.FullScreen1024x768, 0.72m, cancellationToken).ConfigureAwait(false);
        if (takeAll is null)
        {
            return false;
        }

        await ClickCenterAsync(takeAll, cancellationToken).ConfigureAwait(false);
        await SettleAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<TradingActionResult> PlaceBuyOrderAsync(
        TradingAction action,
        int overbidMinCopper,
        int overbidMaxCopper,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (IsPaused)
        {
            return new TradingActionResult(action, TradingActionStatus.Skipped, "Paused by operator before starting order.");
        }

        var (tpReady, tpMessage) = await EnsureTradingPostOpenAsync(cancellationToken).ConfigureAwait(false);
        if (!tpReady)
        {
            return new TradingActionResult(action, TradingActionStatus.Failed, tpMessage);
        }

        await GoToRestPositionAsync(cancellationToken).ConfigureAwait(false);
        await CloseItemWindowAsync(cancellationToken).ConfigureAwait(false);

        if (!await SelectBuyTabAsync(cancellationToken).ConfigureAwait(false))
        {
            return new TradingActionResult(action, TradingActionStatus.Failed, "Buy Items tab was not detected.");
        }

        await GoToRestPositionAsync(cancellationToken).ConfigureAwait(false);
        await ResetFiltersAsync(cancellationToken).ConfigureAwait(false);

        if (IsPaused)
        {
            return new TradingActionResult(action, TradingActionStatus.Skipped, "Paused by operator before searching item.");
        }

        if (!await SearchItemAsync(action.ItemName, cancellationToken).ConfigureAwait(false))
        {
            return new TradingActionResult(action, TradingActionStatus.Failed, $"Search field not found when looking up '{action.ItemName}'.");
        }

        await SettleAsync(cancellationToken).ConfigureAwait(false);
        await GoToRestPositionAsync(cancellationToken).ConfigureAwait(false);

        // Scoped to the results header: full-screen matching can also hit the Delivery Box "Items:" label (~0.76).
        var firstItem = await WaitForTemplateAsync("item.png", SearchResultsHeaderRegion, 0.74m, _uiWaitMs, cancellationToken).ConfigureAwait(false);
        if (firstItem is null)
        {
            return new TradingActionResult(action, TradingActionStatus.Failed, $"No search result row appeared for '{action.ItemName}'.");
        }

        // The TP search is a substring match, so the first row can be a different item ("Slice of Allspice Cake" also
        // lists "Slice of Allspice Cake with Ice Cream"). Open rows in order and buy only from the one whose dialog
        // title is the exact item.
        TemplateMatch? buyersAnchor = null;
        var wrongTitles = new List<string>();
        for (var row = 0; row < MaxSearchRowsToTry; row++)
        {
            if (IsPaused)
            {
                return new TradingActionResult(action, TradingActionStatus.Skipped, "Paused by operator before opening item.");
            }

            var rowAnchor = firstItem with { Y = firstItem.Y + row * SearchResultRowStride };
            buyersAnchor = await OpenItemOrderWindowAsync(rowAnchor, "currentBuyers.png", 0.72m, cancellationToken).ConfigureAwait(false);
            if (buyersAnchor is null)
            {
                break;
            }

            var (titleCheck, seenTitle) = await CheckOpenedItemTitleAsync(action.ItemName, cancellationToken).ConfigureAwait(false);
            if (titleCheck == ItemTitleCheck.Unreadable)
            {
                _logger.LogInformation("Could not read the item window title for {Item}; continuing with search row {Row}.", action.ItemName, row + 1);
                break;
            }

            if (titleCheck == ItemTitleCheck.Match)
            {
                if (row > 0)
                {
                    _logger.LogInformation("Found '{Item}' at search row {Row}.", action.ItemName, row + 1);
                }

                break;
            }

            wrongTitles.Add(seenTitle ?? "?");
            _logger.LogInformation("Search row {Row} for '{Item}' opened '{Other}'; trying the next row.", row + 1, action.ItemName, seenTitle);
            await CloseItemWindowAsync(cancellationToken).ConfigureAwait(false);
            buyersAnchor = null;
        }

        if (buyersAnchor is null)
        {
            await CloseItemWindowAsync(cancellationToken).ConfigureAwait(false);
            return new TradingActionResult(
                action,
                TradingActionStatus.Failed,
                wrongTitles.Count > 0
                    ? $"None of the search rows opened '{action.ItemName}' (saw: {string.Join(", ", wrongTitles.Select(t => $"'{t}'"))}); not buying a different item."
                    : $"Item order window did not open for '{action.ItemName}'.");
        }

        // Wait for async order-book rows from the GW2 server before selecting Row 1 under Current Buyers.
        await SettleAsync(cancellationToken).ConfigureAwait(false);
        await _input.ClickAsync(CenterX(buyersAnchor), CenterY(buyersAnchor) + 48, cancellationToken).ConfigureAwait(false);
        await SettleShortAsync(cancellationToken).ConfigureAwait(false);

        if (_requireTemplateFilesOnDisk)
        {
            for (var selectTry = 0; selectTry < 3; selectTry++)
            {
                if (IsPaused)
                {
                    await CloseItemWindowAsync(cancellationToken).ConfigureAwait(false);
                    return new TradingActionResult(action, TradingActionStatus.Skipped, "Paused by operator before submitting buy order.");
                }

                if (await TryFindOnceAsync("placeOrder.png", CaptureRegion.FullScreen1024x768, 0.72m, cancellationToken).ConfigureAwait(false) is not null)
                {
                    break;
                }

                await SettleAsync(cancellationToken).ConfigureAwait(false);
                if (selectTry == 0)
                {
                    await _input.ClickAsync(CenterX(buyersAnchor), CenterY(buyersAnchor) + 48, cancellationToken).ConfigureAwait(false);
                }
                else if (selectTry == 1)
                {
                    await _input.ClickAsync(Math.Max(10, CenterX(buyersAnchor) - 40), CenterY(buyersAnchor) + 46, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var downArrow = await FindPriceSpinnerArrowAsync("arrow_down.png", cancellationToken).ConfigureAwait(false);
                    if (downArrow is not null)
                    {
                        await ClickCenterAsync(downArrow, cancellationToken).ConfigureAwait(false);
                    }
                }

                await SettleShortAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        var qty = Math.Clamp(action.Quantity, 1L, 250L);
        if (qty > 1)
        {
            var qtyBox = await WaitForTemplateAsync("quantity.png", CaptureRegion.FullScreen1024x768, 0.68m, _uiWaitMs, cancellationToken).ConfigureAwait(false);
            if (qtyBox is not null)
            {
                await _input.ClickAsync(CenterX(qtyBox) + 112, CenterY(qtyBox), cancellationToken).ConfigureAwait(false);
                await ReplaceFieldTextAsync(qty.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
            }
        }

        var overbidSteps = PickStepCount(overbidMinCopper, overbidMaxCopper);
        for (var i = 0; i < overbidSteps; i++)
        {
            var upArrow = await FindPriceSpinnerArrowAsync("arrow_up.png", cancellationToken).ConfigureAwait(false);
            if (upArrow is null) break;
            await ClickCenterAsync(upArrow, cancellationToken).ConfigureAwait(false);
        }

        if (IsPaused)
        {
            await CloseItemWindowAsync(cancellationToken).ConfigureAwait(false);
            return new TradingActionResult(action, TradingActionStatus.Skipped, "Paused by operator before submitting buy order.");
        }

        // A message box over the dialog would swallow the Place Order click while the order is reported as placed.
        await DismissTransientPopupAsync("buy order", cancellationToken).ConfigureAwait(false);
        var placeButton = await WaitForTemplateAsync("placeOrder.png", CaptureRegion.FullScreen1024x768, 0.72m, _uiWaitMs, cancellationToken).ConfigureAwait(false);
        if (placeButton is null)
        {
            await CloseItemWindowAsync(cancellationToken).ConfigureAwait(false);
            return new TradingActionResult(action, TradingActionStatus.Failed, $"Place Order button was not visible for '{action.ItemName}'.");
        }

        if (_textLocator is not null)
        {
            var visibleUnitPrice = await ReadDialogUnitPriceAsync(cancellationToken).ConfigureAwait(false);
            var check = EvaluateVisibleBuyPrice(action.PriceCopper, overbidMaxCopper, visibleUnitPrice);
            _logger.LogInformation("Buy price check for {Item}: {Message}", action.ItemName, check.Message);
            if (check.Abort)
            {
                await CloseItemWindowAsync(cancellationToken).ConfigureAwait(false);
                return new TradingActionResult(action, TradingActionStatus.Failed, $"Buy order aborted: {check.Message}");
            }
        }

        await ClickCenterAsync(placeButton, cancellationToken).ConfigureAwait(false);
        await SettleAsync(cancellationToken).ConfigureAwait(false);
        await CloseItemWindowAsync(cancellationToken).ConfigureAwait(false);

        return new TradingActionResult(
            action,
            TradingActionStatus.Completed,
            $"Placed live buy order for {qty}x '{action.ItemName}' (+{overbidSteps}c overbid).");
    }

    /// <summary>
    /// Refuses unfiltered bag selling so unrelated personal food/consumables in inventory are never sold.
    /// Call the overload with an explicit allow-list of flip item names instead.
    /// </summary>
    public Task<int> SellInventoryPassAsync(
        int maxItemsPerPass,
        int undercutMinCopper,
        int undercutMaxCopper,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(0);

    /// <summary>
    /// Lists ONLY explicit flip items from the character's bags by typing each allowed item name into
    /// Sell Items Search first, checking that it actually exists in bags, and never clicking Sell Instantly.
    /// </summary>
    public async Task<int> SellInventoryPassAsync(
        IReadOnlyList<string> allowedFlipItemNames,
        int maxItemsPerPass,
        int undercutMinCopper,
        int undercutMaxCopper,
        CancellationToken cancellationToken = default)
    {
        var results = await SellInventoryPassDetailedAsync(
            allowedFlipItemNames,
            maxItemsPerPass,
            undercutMinCopper,
            undercutMaxCopper,
            cancellationToken).ConfigureAwait(false);
        return results.Count(r => r.Outcome == SellPassItemOutcome.Listed);
    }

    /// <summary>
    /// Executes the filtered bag sell pass and returns per-item outcomes so the caller can remove listed items
    /// and apply retry backoff to items not yet in the active character's bags.
    /// </summary>
    public async Task<IReadOnlyList<SellPassItemResult>> SellInventoryPassDetailedAsync(
        IReadOnlyList<string> allowedFlipItemNames,
        int maxItemsPerPass,
        int undercutMinCopper,
        int undercutMaxCopper,
        CancellationToken cancellationToken = default)
    {
        if (maxItemsPerPass <= 0 || allowedFlipItemNames is null || allowedFlipItemNames.Count == 0)
        {
            return Array.Empty<SellPassItemResult>();
        }

        var targets = allowedFlipItemNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maxItemsPerPass)
            .ToArray();
        if (targets.Length == 0)
        {
            return Array.Empty<SellPassItemResult>();
        }

        var results = new List<SellPassItemResult>(targets.Length);
        var sellTab = await TryFindOnceAsync("items_tab.png", SellTabRegion, 0.72m, cancellationToken).ConfigureAwait(false);
        if (sellTab is not null)
        {
            await ClickCenterAsync(sellTab, cancellationToken).ConfigureAwait(false);
        }
        else if (_requireTemplateFilesOnDisk)
        {
            await _input.ClickAsync(DeterministicSellTabPoint.X, DeterministicSellTabPoint.Y, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            return results;
        }

        await SettleAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            foreach (var targetItem in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await DismissTransientPopupAsync("sell pass", cancellationToken).ConfigureAwait(false);
                if (IsPaused || await HasErrorPopupAsync(cancellationToken).ConfigureAwait(false))
                {
                    results.Add(new SellPassItemResult(targetItem, SellPassItemOutcome.SkippedError, "Paused or error popup active."));
                    break;
                }

                await GoToRestPositionAsync(cancellationToken).ConfigureAwait(false);
                await ResetFiltersAsync(cancellationToken).ConfigureAwait(false);

                if (!await SearchItemAsync(targetItem, cancellationToken).ConfigureAwait(false))
                {
                    results.Add(new SellPassItemResult(targetItem, SellPassItemOutcome.SkippedError, "Search input unavailable."));
                    continue;
                }

                await SettleAsync(cancellationToken).ConfigureAwait(false);

                if (await TryFindOnceAsync("no_items.png", CaptureRegion.FullScreen1024x768, 0.75m, cancellationToken).ConfigureAwait(false) is not null)
                {
                    results.Add(new SellPassItemResult(targetItem, SellPassItemOutcome.NotFoundInBags, "Not present in current character bags."));
                    continue;
                }

                var firstItem = await WaitForTemplateAsync("item.png", CaptureRegion.FullScreen1024x768, 0.74m, _uiWaitMs, cancellationToken).ConfigureAwait(false);
                if (firstItem is null)
                {
                    results.Add(new SellPassItemResult(targetItem, SellPassItemOutcome.NotFoundInBags, "No sellable item row appeared."));
                    continue;
                }

                var sellersAnchor = await OpenItemOrderWindowAsync(firstItem, "currentSellers.png", 0.72m, cancellationToken).ConfigureAwait(false);
                if (sellersAnchor is null)
                {
                    await CloseItemWindowAsync(cancellationToken).ConfigureAwait(false);
                    results.Add(new SellPassItemResult(targetItem, SellPassItemOutcome.SkippedError, "Order modal did not open."));
                    continue;
                }

                await SettleAsync(cancellationToken).ConfigureAwait(false);
                await _input.ClickAsync(CenterX(sellersAnchor), CenterY(sellersAnchor) + 48, cancellationToken).ConfigureAwait(false);
                await SettleShortAsync(cancellationToken).ConfigureAwait(false);

                var maxSell = await TryFindOnceAsync("choose_max_quantity_sell.png", CaptureRegion.FullScreen1024x768, 0.80m, cancellationToken).ConfigureAwait(false)
                              ?? await TryFindOnceAsync("choose_max_quantity.png", CaptureRegion.FullScreen1024x768, 0.80m, cancellationToken).ConfigureAwait(false);
                if (maxSell is not null)
                {
                    await ClickCenterAsync(maxSell, cancellationToken).ConfigureAwait(false);
                }

                var undercutSteps = PickStepCount(undercutMinCopper, undercutMaxCopper);
                var appliedSteps = 0;
                for (var u = 0; u < undercutSteps; u++)
                {
                    var downArrow = await FindPriceSpinnerArrowAsync("arrow_down.png", cancellationToken).ConfigureAwait(false);
                    if (downArrow is null) break;
                    await ClickCenterAsync(downArrow, cancellationToken).ConfigureAwait(false);
                    appliedSteps++;
                }

                if (appliedSteps > 0 &&
                    await TryFindOnceAsync("minimum_unit_price_reached.png", CaptureRegion.FullScreen1024x768, 0.78m, cancellationToken).ConfigureAwait(false) is not null)
                {
                    for (var r = 0; r < appliedSteps; r++)
                    {
                        var upArrow = await FindPriceSpinnerArrowAsync("arrow_up.png", cancellationToken).ConfigureAwait(false);
                        if (upArrow is null) break;
                        await ClickCenterAsync(upArrow, cancellationToken).ConfigureAwait(false);
                    }
                }

                if (IsPaused)
                {
                    await CloseItemWindowAsync(cancellationToken).ConfigureAwait(false);
                    results.Add(new SellPassItemResult(targetItem, SellPassItemOutcome.SkippedError, "Paused before listing."));
                    break;
                }

                // Never click sell_instantly.png — always place a sell order via List This Item.
                var listButton = await WaitForTemplateAsync("ListThisItem.png", CaptureRegion.FullScreen1024x768, 0.70m, _uiWaitMs, cancellationToken).ConfigureAwait(false);
                if (listButton is not null && !IsPaused)
                {
                    await ClickCenterAsync(listButton, cancellationToken).ConfigureAwait(false);
                    await SettleAsync(cancellationToken).ConfigureAwait(false);
                    results.Add(new SellPassItemResult(targetItem, SellPassItemOutcome.Listed, "Sell listing placed."));
                }
                else
                {
                    results.Add(new SellPassItemResult(targetItem, SellPassItemOutcome.SkippedError, "List This Item button not detected."));
                }

                await CloseItemWindowAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await CloseItemWindowAsync(cancellationToken).ConfigureAwait(false);
            await ResetFiltersAsync(cancellationToken).ConfigureAwait(false);
            _ = await SelectBuyTabAsync(cancellationToken).ConfigureAwait(false);
        }

        return results;
    }

    public async Task<int> CancelOutbidBuyOrdersAsync(
        IReadOnlyList<Transaction> transactions,
        int maxCancelsPerPass,
        CancellationToken cancellationToken = default)
    {
        if (maxCancelsPerPass <= 0 || transactions.Count == 0)
        {
            return 0;
        }

        // Only buy orders are canceled when outbid (canceling buys is 100% free; canceling sells burns 5% fee).
        var nowUtc = _utcNow();
        if (Gw2MarketCycle.Describe(nowUtc).PhaseName == "Daily Reset Spike")
        {
            // The reset window is when sellers dump into buy orders; keep every order standing to catch those fills.
            _logger.LogDebug("Outbid Refresh paused during the daily reset window.");
            return 0;
        }

        var outbidBuys = SelectSafeOutbidCancels(transactions, maxCancelsPerPass, nowUtc, out var ambiguousNames, out var keptYoung);
        if (keptYoung > 0)
        {
            _logger.LogInformation(
                "Outbid Refresh kept {Count} outbid buy order(s) younger than {Minutes} min so they can still fill.",
                keptYoung,
                (int)MinOrderAgeBeforeOutbidCancel.TotalMinutes);
        }

        foreach (var name in ambiguousNames)
        {
            _logger.LogInformation(
                "Outbid Refresh skipped {Item}: searching it would list more than one of your buy orders, so the first Cancel button might not be the outbid one.",
                name);
        }

        if (outbidBuys.Length == 0)
        {
            return 0;
        }

        var txTab = await WaitForTemplateAsync("transactions.png", TransactionsTabRegion, 0.72m, _uiWaitMs, cancellationToken).ConfigureAwait(false)
                    ?? await TryFindOnceAsync("transactions.png", TopHeaderRegion, 0.75m, cancellationToken).ConfigureAwait(false);
        if (txTab is null)
        {
            return 0;
        }

        await ClickCenterAsync(txTab, cancellationToken).ConfigureAwait(false);
        await SettleAsync(cancellationToken).ConfigureAwait(false);
        await ResetFiltersAsync(cancellationToken).ConfigureAwait(false);

        var currentBtn = await WaitForTemplateAsync("transactions_current.png", CaptureRegion.FullScreen1024x768, 0.75m, _uiWaitMs, cancellationToken).ConfigureAwait(false);
        if (currentBtn is not null)
        {
            await ClickCenterAsync(currentBtn, cancellationToken).ConfigureAwait(false);
            await SettleAsync(cancellationToken).ConfigureAwait(false);
        }

        if (await TryFindOnceAsync("at_transactions_buying.png", CaptureRegion.FullScreen1024x768, 0.75m, cancellationToken).ConfigureAwait(false) is null)
        {
            var buyingBtn = await WaitForTemplateAsync("transactions_buying.png", CaptureRegion.FullScreen1024x768, 0.75m, _uiWaitMs, cancellationToken).ConfigureAwait(false);
            if (buyingBtn is not null)
            {
                await ClickCenterAsync(buyingBtn, cancellationToken).ConfigureAwait(false);
                await SettleAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        var canceled = 0;
        foreach (var order in outbidBuys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsPaused) break;
            await DismissTransientPopupAsync("Outbid Refresh", cancellationToken).ConfigureAwait(false);
            if (!await SearchItemAsync(order.ItemName, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            if (IsPaused) break;
            await SettleAsync(cancellationToken).ConfigureAwait(false);
            var cancelBtn = await WaitForTemplateAsync("transactions_cancel.png", TopHeaderRegion, 0.75m, _uiWaitMs, cancellationToken).ConfigureAwait(false)
                            ?? await TryFindOnceAsync("cancel.png", TopHeaderRegion, 0.75m, cancellationToken).ConfigureAwait(false);
            if (cancelBtn is null)
            {
                continue;
            }

            if (IsPaused) break;
            await ClickCenterAsync(cancelBtn, cancellationToken).ConfigureAwait(false);
            await SettleShortAsync(cancellationToken).ConfigureAwait(false);

            var confirmBtn = await WaitForTemplateAsync("transactions_confirm.png", TopHeaderRegion, 0.75m, _uiWaitMs, cancellationToken).ConfigureAwait(false)
                             ?? await TryFindOnceAsync("confirm.png", TopHeaderRegion, 0.75m, cancellationToken).ConfigureAwait(false);
            if (confirmBtn is not null && !IsPaused)
            {
                await ClickCenterAsync(confirmBtn, cancellationToken).ConfigureAwait(false);
                await SettleAsync(cancellationToken).ConfigureAwait(false);
                canceled++;
                _logger.LogInformation(
                    "Outbid Refresh canceled buy order for {Item}: our bid {OurBid}c, API top bid {TopBid}c.",
                    order.ItemName,
                    order.PriceCopper,
                    order.MinimumBuyPriceCopper);
            }
        }

        return canceled;
    }

    public async Task<(bool Succeeded, string Message)> RotateParkedCharacterAsync(
        bool randomize,
        int parkedCharacterCount,
        CancellationToken cancellationToken = default)
    {
        if (parkedCharacterCount <= 1)
        {
            return (true, "Single parked character configured; swap skipped.");
        }

        var width = _settings.WindowWidth > 0 ? _settings.WindowWidth : 1024;
        var height = _settings.WindowHeight > 0 ? _settings.WindowHeight : 768;
        await _windows.EnsureWindowAsync(Gw2ClientSettings.DefaultWindowName, width, height, cancellationToken).ConfigureAwait(false);

        await CloseAllTpWindowsAsync(cancellationToken).ConfigureAwait(false);
        if (_requireTemplateFilesOnDisk)
        {
            await _input.ClickAsync(DeterministicRestPoint.X, DeterministicRestPoint.Y, cancellationToken).ConfigureAwait(false);
            await SettleShortAsync(cancellationToken).ConfigureAwait(false);
        }

        // Esc reliably opens the in-game Game Menu on GW2 where logout.png sits at (409, 425).
        await _input.KeyPressAsync("escape", cancellationToken).ConfigureAwait(false);
        var logoutBtn = await WaitForTemplateAsync("logout.png", CaptureRegion.FullScreen1024x768, 0.74m, Math.Max(40, _uiWaitMs / 2), cancellationToken).ConfigureAwait(false);
        if (logoutBtn is null)
        {
            await _input.KeyPressAsync("escape", cancellationToken).ConfigureAwait(false);
            logoutBtn = await WaitForTemplateAsync("logout.png", CaptureRegion.FullScreen1024x768, 0.74m, Math.Max(40, _uiWaitMs / 2), cancellationToken).ConfigureAwait(false);
        }

        if (logoutBtn is not null)
        {
            await ClickCenterAsync(logoutBtn, cancellationToken).ConfigureAwait(false);
            await SettleShortAsync(cancellationToken).ConfigureAwait(false);
            await _input.KeyPressAsync("enter", cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _input.KeyPressAsync("f12", cancellationToken).ConfigureAwait(false);
            await Task.Delay(Math.Min(400, Math.Max(40, _uiWaitMs / 2)), cancellationToken).ConfigureAwait(false);
            await _input.KeyPressAsync("enter", cancellationToken).ConfigureAwait(false);
        }

        var charSelectWaitMs = _requireTemplateFilesOnDisk ? Math.Max(_uiWaitMs * 3, 18_000) : _uiWaitMs * 2;
        var playBtn = await WaitForCharacterSelectPlayAsync(charSelectWaitMs, cancellationToken).ConfigureAwait(false);
        if (playBtn is null)
        {
            return (false, "Character swap aborted: Character select screen did not appear after Esc/F12 logout.");
        }

        var slotOffset = randomize
            ? Random.Shared.Next(1, Math.Clamp(parkedCharacterCount, 2, 5))
            : Math.Clamp(parkedCharacterCount - 1, 1, 4);
        var portraitX = DeterministicCharSlotOnePoint.X + (slotOffset * CharacterSlotStrideX);
        var portraitY = DeterministicCharSlotOnePoint.Y;

        var previousName = await ReadSelectedCharacterNameAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Selecting parked character slot #{Slot} at ({X}, {Y}); currently selected: {Previous}.",
            slotOffset + 1,
            portraitX,
            portraitY,
            previousName ?? "(name unreadable)");
        await _input.ClickAsync(portraitX, portraitY, cancellationToken).ConfigureAwait(false);
        await SettleShortAsync(cancellationToken).ConfigureAwait(false);

        var selectedName = await ReadSelectedCharacterNameAsync(cancellationToken).ConfigureAwait(false);
        if (selectedName is not null && previousName is not null &&
            string.Equals(selectedName, previousName, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Character Select still shows '{Name}' after clicking slot #{Slot}; the portrait click may have missed, so this swap re-enters the same character.",
                selectedName,
                slotOffset + 1);
        }
        else if (selectedName is not null)
        {
            _logger.LogInformation("Character Select now shows '{Name}' (slot #{Slot}).", selectedName, slotOffset + 1);
        }

        await EnterCharacterSlotAsync(
            playBtn,
            portraitX,
            portraitY,
            Math.Min(500, Math.Max(20, _uiWaitMs / 3)),
            cancellationToken).ConfigureAwait(false);
        await SettleAsync(cancellationToken).ConfigureAwait(false);

        var (reopened, openMsg) = await EnsureTradingPostOpenAsync(cancellationToken).ConfigureAwait(false);
        var who = selectedName is null ? $"slot #{slotOffset + 1}" : $"'{selectedName}' (slot #{slotOffset + 1})";
        return reopened
            ? (true, $"Swapped to parked character {who} and reopened Trading Post.")
            : (false, $"Swapped to parked character {who}, but could not reopen Trading Post: {openMsg}");
    }

    internal enum ItemTitleCheck
    {
        Match,
        Mismatch,
        Unreadable
    }

    /// <summary>Reads the open buy dialog's title (raw and contrast-enhanced) and compares it with the expected item.</summary>
    private async Task<(ItemTitleCheck Result, string? SeenTitle)> CheckOpenedItemTitleAsync(string expectedName, CancellationToken cancellationToken)
    {
        if (_textLocator is null)
        {
            return (ItemTitleCheck.Unreadable, null);
        }

        var texts = await ReadTextVariantsAsync(ItemDialogTitleRegion, cancellationToken).ConfigureAwait(false);
        return ClassifyItemTitle(expectedName, texts);
    }

    /// <summary>Reads the buy dialog's Max. Price (per unit) in copper, or null when it cannot be read.</summary>
    private async Task<long?> ReadDialogUnitPriceAsync(CancellationToken cancellationToken)
    {
        var texts = await ReadTextVariantsAsync(ItemDialogMaxPriceRegion, cancellationToken).ConfigureAwait(false);
        foreach (var text in texts)
        {
            if (ParseDialogPrice(text) is { } copper)
            {
                return copper;
            }
        }

        return null;
    }

    /// <summary>OCR lines of a region, first from the raw frame and then from a contrast-enhanced 2x copy.</summary>
    private async Task<IReadOnlyList<string>> ReadTextVariantsAsync(CaptureRegion region, CancellationToken cancellationToken)
    {
        if (_textLocator is null)
        {
            return Array.Empty<string>();
        }

        try
        {
            var frame = await _screenshots.CaptureAsync(CaptureRegion.FullScreen1024x768, cancellationToken).ConfigureAwait(false);
            if (frame.Pixels.Length == 0)
            {
                return Array.Empty<string>();
            }

            var texts = new List<string>();
            var raw = await _textLocator.LocateLinesAsync(frame, region, cancellationToken).ConfigureAwait(false);
            texts.AddRange(raw.OrderBy(line => line.Y).Select(line => line.Text));
            if (FrameVision.EnhanceTextContrast(frame, region) is { } enhanced)
            {
                var lines = await _textLocator.LocateLinesAsync(enhanced, enhanced.Region, cancellationToken).ConfigureAwait(false);
                texts.AddRange(lines.OrderBy(line => line.Y).Select(line => line.Text));
            }

            return texts;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogDebug(exception, "OCR of region {Region} failed.", region);
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Compares OCR'd dialog title lines with the expected item name. Any line close to the expected name is a match
    /// (OCR often drops an apostrophe or misreads a letter); a readable title that is clearly a different item —
    /// including a longer name that merely contains the expected one — is a mismatch.
    /// </summary>
    internal static (ItemTitleCheck Result, string? SeenTitle) ClassifyItemTitle(string expectedName, IEnumerable<string> ocrTexts)
    {
        var expected = NormalizeItemName(expectedName);
        string? bestOther = null;
        var bestOtherLength = 0;
        foreach (var text in ocrTexts)
        {
            var seen = NormalizeItemName(text);
            if (seen.Length < 3)
            {
                continue;
            }

            if (NameSimilarity(expected, seen) >= 0.85)
            {
                return (ItemTitleCheck.Match, text.Trim());
            }

            if (seen.Length > bestOtherLength)
            {
                bestOther = text.Trim();
                bestOtherLength = seen.Length;
            }
        }

        // A short garbage fragment is not evidence of a different item; require a title-sized reading.
        return bestOther is not null && bestOtherLength >= Math.Min(6, expected.Length)
            ? (ItemTitleCheck.Mismatch, bestOther)
            : (ItemTitleCheck.Unreadable, bestOther);
    }

    /// <summary>Parses the dialog's "gold : silver : copper" boxes, e.g. "3469: 69: • 67:" → 34,696,967c.</summary>
    internal static long? ParseDialogPrice(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var numbers = System.Text.RegularExpressions.Regex.Matches(text, @"\d[\d,]*")
            .Select(match => match.Value.Replace(",", string.Empty, StringComparison.Ordinal))
            .Where(value => value.Length > 0)
            .ToArray();
        if (numbers.Length < 3 ||
            !long.TryParse(numbers[0], out var gold) ||
            !int.TryParse(numbers[1], out var silver) ||
            !int.TryParse(numbers[2], out var copper) ||
            silver is < 0 or > 99 ||
            copper is < 0 or > 99 ||
            gold is < 0 or > 100_000)
        {
            return null;
        }

        return gold * 10_000 + silver * 100L + copper;
    }

    internal static string NormalizeItemName(string? name) =>
        new((name ?? string.Empty).ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>1 - Levenshtein distance / longer length.</summary>
    internal static double NameSimilarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return 1.0 - (double)previous[b.Length] / Math.Max(a.Length, b.Length);
    }

    /// <summary>Reads the large name of the currently selected character on Character Select, or null if unreadable.</summary>
    private async Task<string?> ReadSelectedCharacterNameAsync(CancellationToken cancellationToken)
    {
        if (_textLocator is null)
        {
            return null;
        }

        try
        {
            var frame = await _screenshots.CaptureAsync(CaptureRegion.FullScreen1024x768, cancellationToken).ConfigureAwait(false);
            if (frame.Pixels.Length == 0)
            {
                return null;
            }

            var lines = await _textLocator.LocateLinesAsync(frame, CharSelectNameRegion, cancellationToken).ConfigureAwait(false);
            var name = lines
                .Where(line => line.Text.Trim().Length >= 3)
                .OrderByDescending(line => line.Height)
                .Select(line => line.Text.Trim())
                .FirstOrDefault();
            return name;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogDebug(exception, "Could not read the selected character name.");
            return null;
        }
    }

    /// <summary>
    /// Closes any open Trading Post dialogs, opens the Game Menu, and performs an in-game Exit to Desktop
    /// or Log Out so character/TP state flushes cleanly before closing the game executable.
    /// </summary>
    public async Task<(bool LoggedOut, string Message)> GracefulLogoutAndCloseGameAsync(
        CancellationToken cancellationToken = default)
    {
        var width = _settings.WindowWidth > 0 ? _settings.WindowWidth : 1024;
        var height = _settings.WindowHeight > 0 ? _settings.WindowHeight : 768;
        var windowReady = await _windows.EnsureWindowAsync(
            Gw2ClientSettings.DefaultWindowName,
            width,
            height,
            cancellationToken).ConfigureAwait(false);
        if (!windowReady)
        {
            return (false, "Guild Wars 2 window not active for in-game logout.");
        }

        await CloseAllTpWindowsAsync(cancellationToken).ConfigureAwait(false);
        if (_requireTemplateFilesOnDisk)
        {
            await _input.ClickAsync(DeterministicRestPoint.X, DeterministicRestPoint.Y, cancellationToken).ConfigureAwait(false);
            await SettleShortAsync(cancellationToken).ConfigureAwait(false);
        }

        await _input.KeyPressAsync("escape", cancellationToken).ConfigureAwait(false);
        await SettleAsync(cancellationToken).ConfigureAwait(false);

        if (_textLocator is not null)
        {
            var menuRegion = new CaptureRegion(300, 220, 420, 380);
            var frame = await _screenshots.CaptureAsync(CaptureRegion.FullScreen1024x768, cancellationToken).ConfigureAwait(false);
            if (frame.Pixels.Length > 0)
            {
                var lines = await _textLocator.LocateLinesAsync(frame, menuRegion, cancellationToken).ConfigureAwait(false);
                var exitDesktop = lines.FirstOrDefault(l =>
                    l.Text.Contains("Exit to Desktop", StringComparison.OrdinalIgnoreCase));
                if (exitDesktop is not null)
                {
                    await _input.ClickAsync(
                        exitDesktop.X + Math.Max(1, exitDesktop.Width / 2),
                        exitDesktop.Y + Math.Max(1, exitDesktop.Height / 2),
                        cancellationToken).ConfigureAwait(false);
                    await Task.Delay(_requireTemplateFilesOnDisk ? 2000 : 20, cancellationToken).ConfigureAwait(false);
                    return (true, "Clicked 'Exit to Desktop' cleanly from the in-game menu.");
                }

                var ocrLogout = lines.FirstOrDefault(l =>
                    l.Text.Contains("Log Out", StringComparison.OrdinalIgnoreCase));
                if (ocrLogout is not null)
                {
                    await _input.ClickAsync(
                        ocrLogout.X + Math.Max(1, ocrLogout.Width / 2),
                        ocrLogout.Y + Math.Max(1, ocrLogout.Height / 2),
                        cancellationToken).ConfigureAwait(false);
                    await SettleShortAsync(cancellationToken).ConfigureAwait(false);
                    await _input.KeyPressAsync("enter", cancellationToken).ConfigureAwait(false);
                    await Task.Delay(_requireTemplateFilesOnDisk ? 2000 : 20, cancellationToken).ConfigureAwait(false);
                    return (true, "Logged out to Character Select cleanly before closing Guild Wars 2.");
                }
            }
        }

        var logoutBtn = await WaitForTemplateAsync(
            "logout.png",
            CaptureRegion.FullScreen1024x768,
            0.74m,
            Math.Max(40, _uiWaitMs / 2),
            cancellationToken).ConfigureAwait(false);
        if (logoutBtn is not null)
        {
            await ClickCenterAsync(logoutBtn, cancellationToken).ConfigureAwait(false);
            await SettleShortAsync(cancellationToken).ConfigureAwait(false);
            await _input.KeyPressAsync("enter", cancellationToken).ConfigureAwait(false);
            await Task.Delay(_requireTemplateFilesOnDisk ? 2000 : 20, cancellationToken).ConfigureAwait(false);
            return (true, "Logged out cleanly before closing Guild Wars 2.");
        }

        return (false, "Game Menu logout button not detected; closing process directly.");
    }

    private async Task<TemplateMatch?> WaitForCharacterSelectPlayAsync(int timeoutMs, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(Math.Max(20, timeoutMs));
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsPaused)
            {
                return null;
            }

            var frame = await _screenshots.CaptureAsync(CaptureRegion.FullScreen1024x768, cancellationToken).ConfigureAwait(false);
            var playBtn = await FindCharacterSelectPlayAsync(frame, cancellationToken).ConfigureAwait(false);
            if (playBtn is not null)
            {
                return playBtn;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return null;
            }

            await Task.Delay(_pollMs, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EnterCharacterSlotAsync(
        TemplateMatch playBtn,
        int portraitX,
        int portraitY,
        int verifyDelayMs,
        CancellationToken cancellationToken)
    {
        await ClickCenterAsync(playBtn, cancellationToken).ConfigureAwait(false);
        await Task.Delay(Math.Min(150, Math.Max(10, _settleMs / 4)), cancellationToken).ConfigureAwait(false);
        await _input.KeyPressAsync("enter", cancellationToken).ConfigureAwait(false);
        if (verifyDelayMs > 0)
        {
            await Task.Delay(verifyDelayMs, cancellationToken).ConfigureAwait(false);
        }

        var frame = await _screenshots.CaptureAsync(CaptureRegion.FullScreen1024x768, cancellationToken).ConfigureAwait(false);
        var stillCharSelect = await FindCharacterSelectPlayAsync(frame, cancellationToken).ConfigureAwait(false);
        if (stillCharSelect is not null)
        {
            await _input.DoubleClickAsync(portraitX, portraitY, cancellationToken).ConfigureAwait(false);
        }

        await WaitForInWorldAsync(cancellationToken).ConfigureAwait(false);
    }

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

        if (await TryFindInFrameAsync(frame, "transactions.png", TransactionsTabRegion, 0.70m, cancellationToken).ConfigureAwait(false) is not null ||
            await TryFindInFrameAsync(frame, "buy_items_tab_open.png", BuyTabRegion, 0.65m, cancellationToken).ConfigureAwait(false) is not null ||
            await TryFindInFrameAsync(frame, "buy_items_tab.png", BuyTabRegion, 0.68m, cancellationToken).ConfigureAwait(false) is not null ||
            await TryFindInFrameAsync(frame, "items_tab.png", SellTabRegion, 0.68m, cancellationToken).ConfigureAwait(false) is not null ||
            await TryFindInFrameAsync(frame, "cog.png", SearchFieldRegion, 0.70m, cancellationToken).ConfigureAwait(false) is not null ||
            await TryFindInFrameAsync(frame, "search.png", SearchFieldRegion, 0.70m, cancellationToken).ConfigureAwait(false) is not null ||
            await TryFindInFrameAsync(frame, "exit_item_window.png", ModalCloseButtonRegion, 0.75m, cancellationToken).ConfigureAwait(false) is not null)
        {
            return true;
        }

        return false;
    }

    private async Task<bool> TrySwitchFromBlackLionToTradingPostAsync(CancellationToken cancellationToken)
    {
        var sidebarTp = await TryFindOnceAsync("tradingpost_tab.png", LeftSidebarTpTabRegion, 0.68m, cancellationToken).ConfigureAwait(false);
        if (sidebarTp is null && _requireTemplateFilesOnDisk)
        {
            var blackLion = await TryFindOnceAsync("black_lion.png", BlackLionCrestRegion, 0.75m, cancellationToken).ConfigureAwait(false);
            if (blackLion is not null)
            {
                await _input.ClickAsync(DeterministicLeftSidebarTpPoint.X, DeterministicLeftSidebarTpPoint.Y, cancellationToken).ConfigureAwait(false);
                await SettleAsync(cancellationToken).ConfigureAwait(false);
                return await IsTradingPostOpenAndActiveAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        if (sidebarTp is null)
        {
            return false;
        }

        _logger.LogInformation("Switching Black Lion window from Gem Store to Trading Post tab via sidebar icon.");
        await ClickCenterAsync(sidebarTp, cancellationToken).ConfigureAwait(false);
        await SettleAsync(cancellationToken).ConfigureAwait(false);
        return await IsTradingPostOpenAndActiveAsync(cancellationToken).ConfigureAwait(false);
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

    private async Task<bool> SelectBuyTabAsync(CancellationToken cancellationToken)
    {
        var buyTab = await WaitForAnyTemplateAsync(
            ["buy_items_tab_open.png", "buy_items_tab.png"],
            BuyTabRegion,
            0.68m,
            _uiWaitMs,
            cancellationToken).ConfigureAwait(false);
        if (buyTab is not null)
        {
            await ClickCenterAsync(buyTab, cancellationToken).ConfigureAwait(false);
            await SettleAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (_requireTemplateFilesOnDisk)
        {
            await _input.ClickAsync(DeterministicBuyTabPoint.X, DeterministicBuyTabPoint.Y, cancellationToken).ConfigureAwait(false);
            await SettleAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    private async Task GoToRestPositionAsync(CancellationToken cancellationToken)
    {
        var blackLion = await TryFindOnceAsync("black_lion.png", BlackLionCrestRegion, 0.75m, cancellationToken).ConfigureAwait(false);
        if (blackLion is not null)
        {
            await ClickCenterAsync(blackLion, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_requireTemplateFilesOnDisk)
        {
            await _input.ClickAsync(DeterministicRestPoint.X, DeterministicRestPoint.Y, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> SearchItemAsync(string itemName, CancellationToken cancellationToken)
    {
        var searchField = await WaitForAnyTemplateAsync(
            ["search.png", "search_focused.png"],
            SearchFieldRegion,
            0.70m,
            Math.Min(_uiWaitMs, Math.Max(40, _uiWaitMs / 3)),
            cancellationToken).ConfigureAwait(false);

        if (searchField is not null)
        {
            await ClickCenterAsync(searchField, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // When text is already in the search input, click near the right end of the search box
            // so the cursor never lands mid-word if selection fails.
            var cog = await WaitForTemplateAsync("cog.png", SearchFieldRegion, 0.70m, _uiWaitMs, cancellationToken).ConfigureAwait(false);
            if (cog is not null)
            {
                await _input.ClickAsync(Math.Max(10, CenterX(cog) - 24), CenterY(cog), cancellationToken).ConfigureAwait(false);
            }
            else if (_requireTemplateFilesOnDisk)
            {
                await _input.ClickAsync(DeterministicSearchBoxPoint.X, DeterministicSearchBoxPoint.Y, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                return false;
            }
        }

        await ReplaceFieldTextAsync(itemName.Trim(), cancellationToken).ConfigureAwait(false);
        await SettleShortAsync(cancellationToken).ConfigureAwait(false);
        await _input.KeyPressAsync("enter", cancellationToken).ConfigureAwait(false);
        await WaitForSearchResultsAsync(itemName, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// On high ping the previous search's rows stay on screen for a while after Enter, and the "Item" header is
    /// always visible, so without this the next click lands on a stale row (a previous, similarly named item, or
    /// for Outbid Refresh a different order's Cancel button). Polls the result list until a row shows the searched
    /// name; on timeout it carries on (the buy flow still verifies the opened item's title) and logs it.
    /// </summary>
    private async Task WaitForSearchResultsAsync(string itemName, CancellationToken cancellationToken)
    {
        if (_textLocator is null)
        {
            return;
        }

        var query = NormalizeItemName(itemName);
        var timeoutMs = _requireTemplateFilesOnDisk ? Math.Max(_uiWaitMs, 4_000) : _uiWaitMs;
        var deadline = _utcNow() + TimeSpan.FromMilliseconds(timeoutMs);
        var started = _utcNow();
        while (true)
        {
            await DismissTransientPopupAsync("search", cancellationToken).ConfigureAwait(false);
            if (await TryFindOnceAsync("no_items.png", SearchResultsListRegion, 0.80m, cancellationToken).ConfigureAwait(false) is not null)
            {
                // "Sorry, no items match this filter." — the search answered; there is simply nothing to click.
                return;
            }

            var texts = await ReadTextVariantsAsync(SearchResultsListRegion, cancellationToken).ConfigureAwait(false);
            if (texts.Any(text => FuzzyContains(NormalizeItemName(text), query)))
            {
                var waited = (_utcNow() - started).TotalMilliseconds;
                if (waited > 1_500)
                {
                    _logger.LogInformation("Search results for '{Item}' appeared after {Ms:0} ms.", itemName, waited);
                }

                return;
            }

            if (_utcNow() >= deadline)
            {
                _logger.LogInformation(
                    "Search results for '{Item}' were not confirmed within {Ms} ms; continuing (the opened item is still verified).",
                    itemName,
                    timeoutMs);
                return;
            }

            await Task.Delay(Math.Max(_pollMs, 250), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>True when <paramref name="text"/> contains <paramref name="query"/>, allowing ~15% OCR noise.</summary>
    internal static bool FuzzyContains(string text, string query)
    {
        if (query.Length == 0 || text.Length == 0)
        {
            return false;
        }

        if (text.Contains(query, StringComparison.Ordinal))
        {
            return true;
        }

        if (text.Length < query.Length * 0.8)
        {
            return false;
        }

        for (var start = 0; start <= Math.Max(0, text.Length - query.Length); start++)
        {
            var window = text.Substring(start, Math.Min(query.Length, text.Length - start));
            if (NameSimilarity(window, query) >= 0.85)
            {
                return true;
            }
        }

        return false;
    }

    private async Task ReplaceFieldTextAsync(string text, CancellationToken cancellationToken)
    {
        await _input.KeyPressAsync("ctrl+a", cancellationToken).ConfigureAwait(false);
        await SettleShortAsync(cancellationToken).ConfigureAwait(false);
        await _input.KeyPressAsync("backspace", cancellationToken).ConfigureAwait(false);
        await SettleShortAsync(cancellationToken).ConfigureAwait(false);
        await _input.KeyPressAsync("ctrl+a", cancellationToken).ConfigureAwait(false);
        await SettleShortAsync(cancellationToken).ConfigureAwait(false);
        await _input.KeyPressAsync("backspace", cancellationToken).ConfigureAwait(false);
        await SettleShortAsync(cancellationToken).ConfigureAwait(false);
        await _input.SendTextAsync(text, cancellationToken).ConfigureAwait(false);
    }

    private async Task CloseItemWindowAsync(CancellationToken cancellationToken)
    {
        var region = _requireTemplateFilesOnDisk
            ? ModalCloseButtonRegion
            : new CaptureRegion(0, 0, 800, 400);
        for (var i = 0; i < 3; i++)
        {
            var close = await TryFindOnceAsync("exit_item_window.png", region, 0.75m, cancellationToken).ConfigureAwait(false);
            if (close is null) break;
            await ClickCenterAsync(close, cancellationToken).ConfigureAwait(false);
            await SettleShortAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CloseAllTpWindowsAsync(CancellationToken cancellationToken)
    {
        await CloseItemWindowAsync(cancellationToken).ConfigureAwait(false);
        for (var i = 0; i < 3; i++)
        {
            var close = await TryFindOnceAsync("exit_window.png", TopHeaderRegion, 0.78m, cancellationToken).ConfigureAwait(false);
            if (close is null) break;
            await ClickCenterAsync(close, cancellationToken).ConfigureAwait(false);
            await SettleShortAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ResetFiltersAsync(CancellationToken cancellationToken)
    {
        var region = new CaptureRegion(0, 0, 900, 300);
        for (var i = 0; i < 3; i++)
        {
            var reset = await TryFindOnceAsync("reset_filter_A.png", region, 0.75m, cancellationToken).ConfigureAwait(false)
                        ?? await TryFindOnceAsync("reset_filter_B.png", region, 0.75m, cancellationToken).ConfigureAwait(false);
            if (reset is null) break;
            await ClickCenterAsync(reset, cancellationToken).ConfigureAwait(false);
            await SettleShortAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private Task<TemplateMatch?> WaitForTemplateAsync(
        string fileName,
        CaptureRegion region,
        decimal minScore,
        int timeoutMs,
        CancellationToken cancellationToken) =>
        WaitForAnyTemplateAsync([fileName], region, minScore, timeoutMs, cancellationToken);

    private async Task<TemplateMatch?> WaitForAnyTemplateAsync(
        IReadOnlyList<string> fileNames,
        CaptureRegion region,
        decimal minScore,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var candidates = new List<(string FileName, string Path)>(fileNames.Count);
        foreach (var fileName in fileNames)
        {
            var path = ResolveTemplatePath(fileName);
            if (!_requireTemplateFilesOnDisk || File.Exists(path))
            {
                candidates.Add((fileName, path));
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsPaused)
            {
                return null;
            }

            foreach (var (fileName, path) in candidates)
            {
                var match = await TryFindOnceAtPathAsync(fileName, path, region, minScore, cancellationToken).ConfigureAwait(false);
                if (match is not null)
                {
                    return match;
                }
            }

            if (DateTimeOffset.UtcNow >= deadline || IsPaused)
            {
                return null;
            }

            await Task.Delay(_pollMs, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task<TemplateMatch?> TryFindOnceAsync(
        string fileName,
        CaptureRegion region,
        decimal minScore,
        CancellationToken cancellationToken)
    {
        var path = ResolveTemplatePath(fileName);
        if (_requireTemplateFilesOnDisk && !File.Exists(path))
        {
            return Task.FromResult<TemplateMatch?>(null);
        }

        return TryFindOnceAtPathAsync(fileName, path, region, minScore, cancellationToken);
    }

    private Task<TemplateMatch?> TryFindInFrameAsync(
        AutomationFrame frame,
        string fileName,
        CaptureRegion region,
        decimal minScore,
        CancellationToken cancellationToken)
    {
        var path = ResolveTemplatePath(fileName);
        if (_requireTemplateFilesOnDisk && !File.Exists(path))
        {
            return Task.FromResult<TemplateMatch?>(null);
        }

        return _templates.FindAsync(
            frame,
            new TemplateDefinition(fileName, path, region, minScore),
            cancellationToken);
    }

    private async Task<TemplateMatch?> TryFindOnceAtPathAsync(
        string fileName,
        string path,
        CaptureRegion region,
        decimal minScore,
        CancellationToken cancellationToken)
    {
        var frame = await _screenshots.CaptureAsync(region, cancellationToken).ConfigureAwait(false);
        return await _templates.FindAsync(
            frame,
            new TemplateDefinition(fileName, path, region, minScore),
            cancellationToken).ConfigureAwait(false);
    }

    private Task ClickCenterAsync(TemplateMatch match, CancellationToken cancellationToken) =>
        _input.ClickAsync(CenterX(match), CenterY(match), cancellationToken);

    private static int CenterX(TemplateMatch match) => match.X + Math.Max(0, match.Width / 2);
    private static int CenterY(TemplateMatch match) => match.Y + Math.Max(0, match.Height / 2);

    private Task SettleAsync(CancellationToken cancellationToken) =>
        _settleMs > 0 ? Task.Delay(_settleMs, cancellationToken) : Task.CompletedTask;

    private Task SettleShortAsync(CancellationToken cancellationToken)
    {
        var shortDelay = Math.Min(_settleMs, 150);
        return shortDelay > 0 ? Task.Delay(shortDelay, cancellationToken) : Task.CompletedTask;
    }

    private static int PickStepCount(int min, int max)
    {
        var low = Math.Max(0, min);
        var high = Math.Max(low, max);
        return high == low ? low : Random.Shared.Next(low, high + 1);
    }

    private string ResolveTemplatePath(string fileName) =>
        string.IsNullOrWhiteSpace(_templateRoot)
            ? fileName
            : Path.Combine(Path.GetFullPath(Environment.ExpandEnvironmentVariables(_templateRoot)), fileName);

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
