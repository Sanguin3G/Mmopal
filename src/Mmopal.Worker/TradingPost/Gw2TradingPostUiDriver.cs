using Microsoft.Extensions.Logging;
using Mmopal.Contracts;
using Mmopal.Gw2;
using Mmopal.Native;
using Mmopal.Worker.Vision;

namespace Mmopal.Worker;

/// <summary>
/// High-ping-aware, bounded UI driver for the Guild Wars 2 Launcher, Character Select, and
/// Trading Post (1024x768). Uses disjoint tab regions, cog-relative search focusing,
/// filtered flip-only selling, and theme-independent launcher geometry.
/// </summary>
public sealed partial class Gw2TradingPostUiDriver
{
    private const int MaxLoginClickAttempts = 3;
    private const int MaxSearchRowsToTry = 3;
    // Measured on live 1024x768 screenshots: results header "Item" at y~227, result rows 58 px apart.
    private const int SearchResultRowStride = 58;
    private static readonly CaptureRegion SearchResultsHeaderRegion = new(320, 195, 470, 60);
    // Delivery Box panel on the TP's left column, expanded (header y~475) or collapsed under a long category list (y~681).
    private static readonly CaptureRegion DeliveryPanelRegion = new(60, 455, 260, 300);
    // Centered in-game message box text (above its OK button).
    private static readonly CaptureRegion PopupTextRegion = new(340, 305, 400, 120);
    // Right-hand result list (Buy/Sell Items rows, My Transactions rows); excludes the category panel on the left.
    private static readonly CaptureRegion SearchResultsListRegion = new(325, 240, 450, 300);
    // The first result row's name, alone. Read from the whole list, My Transactions' small orange names came back as
    // "Nika's" or "SWkfio•r of ttk"; this tight crop read "Nika's Leggings" and "Superior Rune of the Renegade" on the
    // same frames (22 Nov). The filter chip above the list was no better ("Search : Mlka± L—ings").
    private static readonly CaptureRegion SearchFirstRowNameRegion = new(385, 258, 300, 28);
    // Short names next to empty space or a price defeat the OCR in the wide crop ("Flare Dye", "Maw of the Damned"): 21
    // items could not be confirmed on 22-23 Nov, so their outbid orders were never cancelled. These crops read them.
    private static readonly CaptureRegion SearchBoxTextRegion = new(72, 186, 205, 24);
    private static readonly CaptureRegion SearchBoxWideTextRegion = new(66, 182, 220, 32);
    private static readonly CaptureRegion SearchFirstRowTightRegion = new(388, 262, 200, 20);
    private static readonly CaptureRegion SearchFirstRowShortRegion = new(388, 258, 120, 28);
    // Buy dialog: item name title at ~(347,175) and the Max. Price gold/silver/copper boxes at y~302-322.
    private static readonly CaptureRegion ItemDialogTitleRegion = new(330, 165, 500, 45);
    // Both lines of a title that wraps.
    private static readonly CaptureRegion ItemDialogTwoLineTitleRegion = new(330, 165, 500, 78);
    // Rows include their "Max. Price" / "Total Price:" labels: on live frames Windows OCR read nothing from tighter
    // crops of just the digits.
    private static readonly CaptureRegion ItemDialogMaxPriceRegion = new(260, 290, 400, 44);
    // Current Buyers list in the order dialog (rows from y~514, price column from x~405).
    private static readonly CaptureRegion BuyersListRegion = new(245, 505, 270, 190);
    private const int BuyersPriceColumnMinX = 395;
    // Rows are 27 px apart, the first one centred 22 px into the list region.
    private const int TopRowHeight = 37;
    // Current Sellers list (price column from x ~690); read before the seller-row click, which inserts our own
    // "Selling 1/N" preview row at the top.
    private static readonly CaptureRegion SellersListRegion = new(530, 505, 270, 190);
    private const int SellersPriceColumnMinX = 680;
    // Sell Items results: rows ~60 px apart (236 / 296 / 356 on a live frame).
    private const int SellResultRowStride = 60;
    // "Total Price: 1 17 98" under Max./Min. Price: plain text without spinners. Ends at x 640: the sell dialog
    // prints "Listing Fee" / "Exchange Fee" from x ~658, which would garble the read.
    private static readonly CaptureRegion ItemDialogTotalPriceRegion = new(260, 330, 380, 44);
    internal static readonly TimeSpan MinOrderAgeBeforeOutbidCancel = TimeSpan.FromMinutes(20);
    internal static readonly TimeSpan ResetWindowCancelAge = TimeSpan.FromMinutes(60);
    // A buy order still at the top bid after this long is not filling: of the 348 fills placed 15-21 Nov, 97% (98% of the
    // gold) filled within 24 h, while on 23 Nov ~333g of the 427g in buy orders was older than that and the gold cap
    // blocked buys in 316 of 322 cycles. Cancelling a buy order is free.
    internal static readonly TimeSpan StaleBuyOrderAge = TimeSpan.FromHours(24);

    // Per item: search confirmations seen / missed. Some names never confirm on screen (Tyria's Best Nougat Center
    // 24 of 24, Unstable Rift Motivation 22 of 22), and each miss waited the full 7.5 s: 41 min in a 7 h session.
    private readonly Dictionary<string, (int Seen, int Missed)> _searchConfirmStats = new(StringComparer.OrdinalIgnoreCase);
    // A third of the UI wait (1.5-3 s): results that do appear show up in ~1.5 s at high ping (21-22 Nov), so the
    // short wait follows the latency mode like every other UI wait instead of a fixed 2 s.
    private int ShortSearchWaitMs => Math.Clamp(_uiWaitMs / 3, 1_500, 3_000);

    /// <summary>Items whose buy orders the last Outbid Refresh pass cancelled (for the bot-war guard).</summary>
    public IReadOnlyList<string> LastCanceledOutbidItems { get; private set; } = Array.Empty<string>();

    /// <summary>Items whose cancel search was not confirmed since the last call.</summary>
    public IReadOnlyList<string> TakeUnconfirmedCancels()
    {
        var items = _unconfirmedCancels.ToArray();
        _unconfirmedCancels.Clear();
        return items;
    }

    private readonly List<string> _unconfirmedCancels = [];

    /// <summary>Items whose old, unfilled top-bid buy orders the last Outbid Refresh pass cancelled.</summary>
    public IReadOnlyList<string> LastCanceledStaleItems { get; private set; } = Array.Empty<string>();

    // Slot the last swap picked: if Character Select shows up again right after, enter the same slot instead of a new
    // random one (a swap picked slot #3, then the follow-up picked slot #4 and logged in twice).
    private (int Offset, DateTimeOffset At)? _pendingSlot;
    private readonly HashSet<string> _ambiguousOutbidLogged = new(StringComparer.OrdinalIgnoreCase);
    // Cancel searches that could not be confirmed, per item: the 23 min session on 22 Nov searched Ebonblade 6 times
    // and Elder Draco-Pop, Reclaimed Torch and Rune of the Renegade 4-5 times each without cancelling anything.
    // Normalized names of every order in the list a cancel flow searches (null outside those flows).
    private IReadOnlyCollection<string>? _cancelListNames;
    // The unit price of the order being cancelled when no other order in that list has it (null outside those flows).
    private long? _cancelRowPrice;
    // My Transactions: the first row's Price column, and where the second row's Cancel button would be.
    private static readonly CaptureRegion TransactionFirstPriceRegion = new(740, 258, 110, 30);
    private static readonly CaptureRegion TransactionSecondCancelRegion = new(885, 300, 95, 70);
    private const int LongestSearchText = 60;
    private const int LongestNumberText = 6;
    // Per list (Buying or Selling): a missing buy order says nothing about a listing of the same item.
    private readonly Dictionary<string, (int Count, DateTimeOffset At)> _unconfirmedCancelSearches = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan CancelSearchRetryAfter = TimeSpan.FromHours(1);
    // How often the search was given up: each time it is left alone twice as long (Rune of the Mesmer was tried 11 times in
    // one night, twice an hour).
    private readonly Dictionary<string, int> _cancelGiveUps = new(StringComparer.OrdinalIgnoreCase);
    // Names the search list never shows readably although the right item opens ("Imryldyeen", "Al'ir'aska").
    private readonly HashSet<string> _unreadableNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Items whose name cannot be read in the search list, learned this session or given by <see cref="SeedUnreadableNames"/>.</summary>
    public IReadOnlyCollection<string> UnreadableNames => _unreadableNames;

    public void SeedUnreadableNames(IEnumerable<string> names) => _unreadableNames.UnionWith(names.Select(name => name.Trim()));

    /// <summary>The right item opened without its name having shown in the search list.</summary>
    private void NoteOpenedItem(string itemName)
    {
        if (!_lastSearchShowedName && _textLocator is not null)
        {
            _unreadableNames.Add(itemName.Trim());
        }
    }
    private TransactionType _cancelListType = TransactionType.Buy;
    private const int UnconfirmedCancelSearchesBeforeSkip = 2;
    private bool _lastSearchFoundNothing;
    private bool _lastSearchShowedName;
    // The search box showed the typed name (it then has the focus, and the search applies).
    private bool _lastSearchBoxProven;

    /// <summary>The character entered last ("'Name' (slot #3)" or "slot #3"), for messages; null before any swap.</summary>
    public string? CurrentCharacterLabel { get; private set; }

    /// <summary>The logged-in character's name as read on Character Select, or null when it was not read.</summary>
    public string? CurrentCharacterName { get; private set; }

    /// <summary>How often a character was entered, so what was learned about the previous one can be dropped.</summary>
    public int CharacterEntries { get; private set; }

    /// <summary>Sets the logged-in character when its name could not be read at Character Select but is known otherwise.</summary>
    public void AssumeCurrentCharacter(string name) => CurrentCharacterName ??= name;

    /// <summary>
    /// Names read on the parked portraits (the first min(parked, 5) slots) during a name-guided swap. Characters
    /// beyond them are never entered, so their stock is not a reason to swap.
    /// </summary>
    public IReadOnlyList<string> ParkedCharacterNames { get; private set; } = [];

    /// <summary>
    /// Characters holding unsold stock, most first (set by the session from the bag sync). A launch, game restart or
    /// disconnect that reaches Character Select enters the first of these found on the parked portraits.
    /// </summary>
    public IReadOnlyList<string> PreferredCharacterNames { get; set; } = [];

    /// <summary>Awaited (up to 10 s) at Character Select: fills in <see cref="PreferredCharacterNames"/> on a launch.</summary>
    public Func<CancellationToken, Task>? BeforeCharacterChoice { get; set; }
    private static readonly CaptureRegion TopHeaderRegion = new(0, 0, 1024, 400);
    // Black Lion sidebar scales icon measured at (14..53, 215..234) on 1024x768 (Gem Store and TP tabs alike).
    private static readonly CaptureRegion LeftSidebarTpTabRegion = new(0, 195, 80, 80);
    private static readonly CaptureRegion BuyTabRegion = new(475, 90, 175, 150);
    private static readonly CaptureRegion SellTabRegion = new(645, 90, 170, 150);
    private static readonly CaptureRegion TransactionsTabRegion = new(810, 90, 175, 150);
    private static readonly CaptureRegion SearchFieldRegion = new(0, 180, 330, 140);
    private static readonly CaptureRegion LauncherLoginRegion = new(20, 440, 280, 220);
    // Centre of the launcher's LOG IN button with the launcher snapped to (0, 0): every live click since 20 Nov.
    private static readonly (int X, int Y) LauncherLoginButtonPoint = (123, 585);
    private static readonly CaptureRegion LauncherLoginOcrRegion = new(10, 340, 360, 360);
    private static readonly CaptureRegion LauncherPlayRegion = new(600, 560, 424, 208);
    private static readonly CaptureRegion LauncherPlayOcrRegion = new(540, 510, 484, 258);
    private static readonly CaptureRegion GameMenuRegion = new(300, 220, 420, 380);
    private static readonly CaptureRegion LogoutDialogRegion = new(130, 260, 760, 270);
    private static readonly CaptureRegion CharSelectPlayRegion = new(680, 680, 220, 88);
    private static readonly CaptureRegion CharSelectBottomBarRegion = new(0, 680, 1024, 88);
    // Large name of the selected character on the left of Character Select (e.g. "Mesmerising Rizzo" at ~57,400).
    private static readonly CaptureRegion CharSelectNameRegion = new(40, 375, 440, 50);
    private static readonly CaptureRegion InGameInteractRegion = new(250, 220, 550, 500);
    private static readonly CaptureRegion ModalPriceSpinnerRegion = new(545, 265, 80, 65);
    // The green "Success" box's title, centred over the item window.
    private static readonly CaptureRegion SuccessTitleRegion = new(330, 220, 360, 90);
    private static readonly CaptureRegion ModalCloseButtonRegion = new(720, 120, 90, 80);
    private static readonly CaptureRegion BlackLionCrestRegion = new(0, 0, 120, 120);

    private static readonly (int X, int Y) DeterministicLeftSidebarTpPoint = (33, 224);
    private static readonly (int X, int Y) DeterministicBuyTabPoint = (562, 165);
    private static readonly (int X, int Y) DeterministicSellTabPoint = (725, 165);
    private static readonly (int X, int Y) DeterministicRestPoint = (115, 65);
    private static readonly (int X, int Y) DeterministicCharSlotOnePoint = (258, 640);
    private const int CharacterSlotStrideX = 122;

    private readonly IScreenshotProvider _screenshots;
    private readonly ITemplateMatcher _templates;
    private readonly IInputController _input;
    private readonly IWindowController _windows;
    private readonly IUiTextLocator? _textLocator;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly string? _diagnosticsDirectory;
    private readonly Dictionary<string, List<DateTimeOffset>> _diagnosticFramesSaved = new(StringComparer.Ordinal);
    private readonly string _templateRoot;
    private readonly Gw2ClientSettings _settings;
    private readonly ILogger _logger;
    private readonly int _uiWaitMs;
    private readonly int _settleMs;
    private readonly int _pollMs;
    private readonly bool _requireTemplateFilesOnDisk;
    private readonly bool _proveTradingPost;
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
        Func<DateTimeOffset>? utcNow = null,
        string? diagnosticsDirectory = null,
        bool? proveTradingPost = null)
    {
        _proveTradingPost = proveTradingPost ?? requireTemplateFilesOnDisk;
        _diagnosticsDirectory = diagnosticsDirectory;
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

    // A two-line title pushes the rest of the item window down. The regions are measured on one-line titles, so they
    // follow the order book's header: where it usually is, and how much lower it is now.
    private readonly Dictionary<string, int> _usualAnchorY = new(StringComparer.Ordinal) { ["currentBuyers.png"] = 452, ["currentSellers.png"] = 466 };
    private (string Template, int Y)? _openDialogAnchor;
    private int _dialogShiftY;

    private CaptureRegion InDialog(CaptureRegion region) => region with { Y = region.Y + _dialogShiftY };

    // The game world has been on screen since the last launcher or Character Select.
    private bool _worldSeen;

    /// <summary>The game was started again: its loading has to be waited for like on a first launch.</summary>
    public void NoteGameRestarted() => _worldSeen = false;

    /// <summary>
    /// True when the last <see cref="EnsureTradingPostOpenAsync"/> exhausted its key attempts on an in-world
    /// character. Recovery uses it to switch to another parked character instead of retrying the same spot.
    /// </summary>
    public bool LastTradingPostOpenFailed { get; private set; }

    /// <summary>True when the last <see cref="CollectDeliveryBoxAsync"/> found the box locked to a trader NPC.</summary>
    public bool DeliveryNeedsTraderNpc { get; private set; }

    /// <summary>The last Delivery Box read showed the text of an empty box.</summary>
    public bool DeliveryLooksEmpty { get; private set; }

    /// <summary>When a "lost its connection ... wait a few minutes" message box was last dismissed, if ever.</summary>
    public DateTimeOffset? ServerDisconnectSeenUtc { get; set; }

    // The Total line's digits, right of the "Total Price:" label (the label's letters would be read as digits).
    private static readonly CaptureRegion GlyphTotalRegion = new(382, 330, 250, 44);

    internal enum ItemTitleCheck
    {
        Match,
        Mismatch,
        Unreadable
    }

    private const int DiagnosticFramesPerHour = 3;
    private const int DiagnosticFramesPerSession = 12;

    /// <summary>
    /// Saves full frames for calibration: up to 3 per kind per hour. Three per session left the 38 minutes of 24 Nov
    /// without a single picture.
    /// </summary>
    private async Task SaveDiagnosticFrameAsync(string tag, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_diagnosticsDirectory) || !MaySaveFrame(tag))
        {
            return;
        }

        try
        {
            var frame = await _screenshots.CaptureAsync(CaptureRegion.FullScreen1024x768, cancellationToken).ConfigureAwait(false);
            var path = Path.Combine(_diagnosticsDirectory, $"{tag}-{_utcNow():yyyyMMdd-HHmmss}.png");
            if (FrameImageFile.TrySavePng(frame, path))
            {
                _diagnosticFramesSaved.TryAdd(tag, []);
                _diagnosticFramesSaved[tag].Add(_utcNow());
                _logger.LogInformation("Saved a calibration screenshot: {Path}", path);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogDebug(exception, "Could not save a diagnostic frame.");
        }
    }

    private bool MaySaveFrame(string tag) =>
        !_diagnosticFramesSaved.TryGetValue(tag, out var saved) ||
        (saved.Count < DiagnosticFramesPerSession && saved.Count(at => _utcNow() - at < TimeSpan.FromHours(1)) < DiagnosticFramesPerHour);

    private Task CloseItemWindowAsync(CancellationToken cancellationToken)
    {
        var region = _requireTemplateFilesOnDisk
            ? ModalCloseButtonRegion
            : new CaptureRegion(0, 0, 800, 400);
        return ClickWhileVisibleAsync(
            () => TryFindOnceAsync("exit_item_window.png", region, 0.75m, cancellationToken),
            cancellationToken);
    }

    private async Task CloseAllTpWindowsAsync(CancellationToken cancellationToken)
    {
        await CloseItemWindowAsync(cancellationToken).ConfigureAwait(false);
        await ClickWhileVisibleAsync(
            () => TryFindOnceAsync("exit_window.png", TopHeaderRegion, 0.78m, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private Task ResetFiltersAsync(CancellationToken cancellationToken)
    {
        var region = new CaptureRegion(0, 0, 900, 300);
        return ClickWhileVisibleAsync(
            async () => await TryFindOnceAsync("reset_filter_A.png", region, 0.75m, cancellationToken).ConfigureAwait(false)
                        ?? await TryFindOnceAsync("reset_filter_B.png", region, 0.75m, cancellationToken).ConfigureAwait(false),
            cancellationToken);
    }

    /// <summary>Clicks a close or reset control up to three times, for as long as it is still found.</summary>
    private async Task ClickWhileVisibleAsync(Func<Task<TemplateMatch?>> find, CancellationToken cancellationToken)
    {
        for (var i = 0; i < 3; i++)
        {
            var control = await find().ConfigureAwait(false);
            if (control is null) break;
            await ClickCenterAsync(control, cancellationToken).ConfigureAwait(false);
            await SettleShortAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Snaps the game window to (0, 0) at the configured size (1024x768 when unset).</summary>
    private Task<bool> EnsureGameWindowAsync(CancellationToken cancellationToken)
    {
        var width = _settings.WindowWidth > 0 ? _settings.WindowWidth : 1024;
        var height = _settings.WindowHeight > 0 ? _settings.WindowHeight : 768;
        return _windows.EnsureWindowAsync(Gw2ClientSettings.DefaultWindowName, width, height, cancellationToken);
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
            if (TryResolveTemplate(fileName, out var path))
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
        return TryResolveTemplate(fileName, out var path)
            ? TryFindOnceAtPathAsync(fileName, path, region, minScore, cancellationToken)
            : Task.FromResult<TemplateMatch?>(null);
    }

    private Task<TemplateMatch?> TryFindInFrameAsync(
        AutomationFrame frame,
        string fileName,
        CaptureRegion region,
        decimal minScore,
        CancellationToken cancellationToken)
    {
        return TryResolveTemplate(fileName, out var path)
            ? _templates.FindAsync(frame, new TemplateDefinition(fileName, path, region, minScore), cancellationToken)
            : Task.FromResult<TemplateMatch?>(null);
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

    private async Task ClickCenterAndSettleAsync(TemplateMatch match, CancellationToken cancellationToken)
    {
        await ClickCenterAsync(match, cancellationToken).ConfigureAwait(false);
        await SettleAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Center of an OCR line box (at least 1 px in from its corner), as a click point.</summary>
    private static (int X, int Y) OcrBoxCenter(OcrTextBox box) =>
        (box.X + Math.Max(1, box.Width / 2), box.Y + Math.Max(1, box.Height / 2));

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

    /// <summary>Resolves a template's path; false when template files are required and this one is missing.</summary>
    private bool TryResolveTemplate(string fileName, out string path)
    {
        path = ResolveTemplatePath(fileName);
        return !_requireTemplateFilesOnDisk || File.Exists(path);
    }
}
