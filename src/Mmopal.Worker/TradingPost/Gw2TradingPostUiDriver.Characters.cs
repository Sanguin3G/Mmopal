using Microsoft.Extensions.Logging;
using Mmopal.Contracts;
using Mmopal.Gw2;
using Mmopal.Native;
using Mmopal.Worker.Vision;

namespace Mmopal.Worker;

/// <summary>Parked characters: AFK and recovery swaps, slot choice, reading the selected name, logout at the session cap.</summary>
public sealed partial class Gw2TradingPostUiDriver
{
    /// <summary>
    /// Logs out to Character Select and enters another parked character. With <paramref name="preferredNames"/>
    /// (best first) the portraits are read one by one and the best-ranked name found is entered; otherwise, or when
    /// no name is found, the slot follows <see cref="PickParkedSlotOffset"/>.
    /// </summary>
    public async Task<(bool Succeeded, string Message)> RotateParkedCharacterAsync(
        bool randomize,
        int parkedCharacterCount,
        CancellationToken cancellationToken = default,
        IReadOnlyList<string>? preferredNames = null)
    {
        if (parkedCharacterCount <= 1)
        {
            return (true, "Single parked character configured; swap skipped.");
        }

        await EnsureGameWindowAsync(cancellationToken).ConfigureAwait(false);
        await CloseTpWindowsAndClickRestAsync(cancellationToken).ConfigureAwait(false);

        var (playBtn, logoutProblem) = await LogOutToCharacterSelectAsync(cancellationToken).ConfigureAwait(false);
        if (playBtn is null)
        {
            return (false, $"Character swap aborted: {logoutProblem}");
        }

        if (!await WaitForCharacterSelectReadyAsync(cancellationToken).ConfigureAwait(false))
        {
            return (false, "Character swap aborted: Character Select closed before a character was chosen.");
        }

        var previousName = await ReadSelectedCharacterNameAsync(cancellationToken).ConfigureAwait(false);
        var named = preferredNames is { Count: > 0 }
            ? await FindPortraitByNameAsync(preferredNames, parkedCharacterCount, cancellationToken).ConfigureAwait(false)
            : null;
        if (!await IsCharacterSelectShownAsync(cancellationToken).ConfigureAwait(false))
        {
            return (false, "Character swap aborted: Character Select closed before a character was chosen.");
        }

        var slotOffset = named?.Offset ?? PickParkedSlotOffset(randomize, parkedCharacterCount);
        _pendingSlot = (slotOffset, _utcNow());
        var portraitX = DeterministicCharSlotOnePoint.X + (slotOffset * CharacterSlotStrideX);
        var portraitY = DeterministicCharSlotOnePoint.Y;

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

        var who = selectedName is null ? $"slot #{slotOffset + 1}" : $"'{selectedName}' (slot #{slotOffset + 1})";
        CurrentCharacterLabel = who;
        CurrentCharacterName = selectedName ?? named?.Name;
        var (reopened, openMsg) = await EnsureTradingPostOpenAsync(cancellationToken).ConfigureAwait(false);
        return reopened
            ? (true, $"Swapped to parked character {who} and reopened Trading Post.")
            : (false, $"Swapped to parked character {who}, but could not reopen Trading Post: {openMsg}");
    }

    /// <summary>
    /// Clicks the first min(parked, 5) portraits and reads each name; returns the slot of the best-ranked preferred
    /// name, or null when none was read. Clicking a portrait only selects it. Every portrait is read (about 0.5 s
    /// each) so <see cref="ParkedCharacterNames"/> lists the whole rotation.
    /// </summary>
    private async Task<(int Offset, string Name)?> FindPortraitByNameAsync(
        IReadOnlyList<string> preferredNames,
        int parkedCharacterCount,
        CancellationToken cancellationToken)
    {
        if (_textLocator is null)
        {
            return null;
        }

        (int Offset, string Name, int Rank)? best = null;
        var seen = new List<string>();
        for (var offset = 0; offset < Math.Clamp(parkedCharacterCount, 1, 5); offset++)
        {
            if (IsPaused || !await IsCharacterSelectShownAsync(cancellationToken).ConfigureAwait(false))
            {
                return null; // left Character Select: the next click would land in the world
            }

            await _input.ClickAsync(DeterministicCharSlotOnePoint.X + (offset * CharacterSlotStrideX), DeterministicCharSlotOnePoint.Y, cancellationToken).ConfigureAwait(false);
            await SettleShortAsync(cancellationToken).ConfigureAwait(false);
            var name = await ReadSelectedCharacterNameAsync(cancellationToken).ConfigureAwait(false);
            if (name is null)
            {
                // The name may still be fading in. One more read after a full settle.
                await SettleAsync(cancellationToken).ConfigureAwait(false);
                name = await ReadSelectedCharacterNameAsync(cancellationToken).ConfigureAwait(false);
            }

            if (name is not null)
            {
                seen.Add(name);
            }

            var rank = name is null ? -1 : IndexOfName(preferredNames, name);
            if (rank >= 0 && (best is null || rank < best.Value.Rank))
            {
                best = (offset, preferredNames[rank], rank);
            }
        }

        if (seen.Count > 0)
        {
            ParkedCharacterNames = seen;
        }

        if (seen.Count == 0)
        {
            await SaveDiagnosticFrameAsync("character-select-unread", cancellationToken).ConfigureAwait(false);
        }

        if (best is { } found)
        {
            _logger.LogInformation("Character Select: '{Name}' is in slot #{Slot}; swapping to it.", found.Name, found.Offset + 1);
            return (found.Offset, found.Name);
        }

        _logger.LogInformation("Character Select: none of {Names} was read on the first portraits; using the usual slot choice.", string.Join(", ", preferredNames));
        return null;

        static int IndexOfName(IReadOnlyList<string> names, string read)
        {
            for (var i = 0; i < names.Count; i++)
            {
                if (CharacterStock.NamesMatch(names[i], read)) return i;
            }

            return -1;
        }
    }

    /// <summary>
    /// Zero-based portrait offset on Character Select, which lists the last played character first. With one parked
    /// character it is slot 1; otherwise never slot 1: random picks slot 2..min(N, 5), "next" picks slot N (the
    /// least recently played, which rotates through all parked characters over time).
    /// </summary>
    internal static int PickParkedSlotOffset(bool randomize, int parkedCharacterCount, Func<int, int, int>? nextRandom = null)
    {
        if (parkedCharacterCount <= 1)
        {
            return 0;
        }

        return randomize
            ? (nextRandom ?? Random.Shared.Next)(1, Math.Clamp(parkedCharacterCount, 2, 5))
            : Math.Clamp(parkedCharacterCount - 1, 1, 4);
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
            // In the world this spot shows nameplates: on 22 Nov "Black Lion Tre" was read there as the character.
            if (frame.Pixels.Length == 0 || await FindCharacterSelectPlayAsync(frame, cancellationToken).ConfigureAwait(false) is null)
            {
                return null;
            }

            var lines = await _textLocator.LocateLinesAsync(frame, CharSelectNameRegion, cancellationToken).ConfigureAwait(false);
            return lines
                .Where(line => line.Text.Trim().Length >= 3)
                .OrderByDescending(line => line.Height)
                .Select(line => line.Text.Trim())
                .FirstOrDefault();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogDebug(exception, "Could not read the selected character name.");
            return null;
        }
    }

    /// <summary>
    /// Logs out to Character Select before the game is closed, so the character's state is saved. Reports success only
    /// when Character Select is on screen: "Exit to Desktop" used to be clicked and called clean without a look (22 Nov),
    /// right before the game process was closed.
    /// </summary>
    public async Task<(bool LoggedOut, string Message)> GracefulLogoutAndCloseGameAsync(
        CancellationToken cancellationToken = default)
    {
        if (!await EnsureGameWindowAsync(cancellationToken).ConfigureAwait(false))
        {
            return (false, "Guild Wars 2 window not active for in-game logout.");
        }

        if (await IsCharacterSelectShownAsync(cancellationToken).ConfigureAwait(false))
        {
            return (true, "Already on Character Select; closing Guild Wars 2.");
        }

        await CloseTpWindowsAndClickRestAsync(cancellationToken).ConfigureAwait(false);
        var (characterSelect, problem) = await LogOutToCharacterSelectAsync(cancellationToken).ConfigureAwait(false);
        return characterSelect is null
            ? (false, $"{problem} Closing the game process directly.")
            : (true, "Logged out to Character Select; closing Guild Wars 2.");
    }

    /// <summary>
    /// Game Menu, Log Out, Character Select: returns its Play button once the screen is there, or what went wrong. Used by
    /// every swap and before the game is closed.
    /// </summary>
    private async Task<(TemplateMatch? Play, string Problem)> LogOutToCharacterSelectAsync(CancellationToken cancellationToken)
    {
        // Esc opens the Game Menu; a first Esc may only close something else.
        (int X, int Y)? logOut = null;
        foreach (var key in new[] { "escape", "escape", "f12" })
        {
            await _input.KeyPressAsync(key, cancellationToken).ConfigureAwait(false);
            logOut = await FindMenuLogOutAsync(cancellationToken).ConfigureAwait(false);
            if (logOut is not null || IsPaused)
            {
                break;
            }
        }

        if (logOut is not { } point)
        {
            return (null, "the Game Menu's Log Out button was not found.");
        }

        await _input.ClickAsync(point.X, point.Y, cancellationToken).ConfigureAwait(false);
        await SettleShortAsync(cancellationToken).ConfigureAwait(false);
        await ConfirmLogoutAsync(cancellationToken).ConfigureAwait(false);

        var waitMs = _requireTemplateFilesOnDisk ? Math.Max(_uiWaitMs * 3, 18_000) : _uiWaitMs * 2;
        var play = await WaitForCharacterSelectPlayAsync(waitMs, cancellationToken).ConfigureAwait(false);
        return (play, play is null ? "Character Select did not appear after Log Out." : string.Empty);
    }

    private async Task<(int X, int Y)?> FindMenuLogOutAsync(CancellationToken cancellationToken)
    {
        var template = await WaitForTemplateAsync("logout.png", CaptureRegion.FullScreen1024x768, 0.74m, Math.Max(40, _uiWaitMs / 2), cancellationToken).ConfigureAwait(false);
        if (template is not null)
        {
            return (CenterX(template), CenterY(template));
        }

        if (_textLocator is null)
        {
            return null;
        }

        var frame = await _screenshots.CaptureAsync(CaptureRegion.FullScreen1024x768, cancellationToken).ConfigureAwait(false);
        if (frame.Pixels.Length == 0)
        {
            return null;
        }

        var lines = await _textLocator.LocateLinesAsync(frame, GameMenuRegion, cancellationToken).ConfigureAwait(false);
        // Only in the Game Menu itself, which also lists Exit to Desktop.
        return lines.Any(line => line.Text.Contains("Exit to Desktop", StringComparison.OrdinalIgnoreCase)) &&
               lines.FirstOrDefault(line => line.Text.Trim().Equals("Log Out", StringComparison.OrdinalIgnoreCase)) is { } box
            ? OcrBoxCenter(box)
            : null;
    }

    /// <summary>Closes TP windows, then (live) clicks the rest point so the next key reaches the game, not a dialog.</summary>
    private async Task CloseTpWindowsAndClickRestAsync(CancellationToken cancellationToken)
    {
        await CloseAllTpWindowsAsync(cancellationToken).ConfigureAwait(false);
        if (_requireTemplateFilesOnDisk)
        {
            await _input.ClickAsync(DeterministicRestPoint.X, DeterministicRestPoint.Y, cancellationToken).ConfigureAwait(false);
            await SettleShortAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// After Log Out the game asks "Are you sure you want to log out?" with Log Out, Character Select and Cancel: clicks
    /// Character Select. Enter (what used to confirm it) is only pressed when the question was read and the button was
    /// not: without the dialog Enter opens chat, and on Character Select it enters the selected character.
    /// </summary>
    private async Task ConfirmLogoutAsync(CancellationToken cancellationToken)
    {
        if (_textLocator is null)
        {
            if (await TryFindOnceAsync("logout.png", CaptureRegion.FullScreen1024x768, 0.74m, cancellationToken).ConfigureAwait(false) is not null)
            {
                await _input.KeyPressAsync("enter", cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(Math.Max(_uiWaitMs / 2, _pollMs));
        var sawQuestion = false;
        while (!IsPaused)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = await _screenshots.CaptureAsync(CaptureRegion.FullScreen1024x768, cancellationToken).ConfigureAwait(false);
            if (await FindCharacterSelectPlayAsync(frame, cancellationToken).ConfigureAwait(false) is not null)
            {
                return;
            }

            if (frame.Pixels.Length > 0)
            {
                var lines = await _textLocator.LocateLinesAsync(frame, LogoutDialogRegion, cancellationToken).ConfigureAwait(false);
                sawQuestion |= lines.Any(line => FuzzyContains(NormalizeItemName(line.Text), "areyousureyouwanttologout"));
                var button = lines.FirstOrDefault(line => NameSimilarity(NormalizeItemName(line.Text), "characterselect") >= 0.85);
                if (sawQuestion && button is not null)
                {
                    var (x, y) = OcrBoxCenter(button);
                    _logger.LogInformation("Confirming the logout with Character Select at ({X}, {Y}).", x, y);
                    await _input.ClickAsync(x, y, cancellationToken).ConfigureAwait(false);
                    return;
                }
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                break;
            }

            await Task.Delay(Math.Max(_pollMs, 150), cancellationToken).ConfigureAwait(false);
        }

        if (sawQuestion && !IsPaused)
        {
            await _input.KeyPressAsync("enter", cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> IsCharacterSelectShownAsync(CancellationToken cancellationToken)
    {
        var frame = await _screenshots.CaptureAsync(CaptureRegion.FullScreen1024x768, cancellationToken).ConfigureAwait(false);
        return await FindCharacterSelectPlayAsync(frame, cancellationToken).ConfigureAwait(false) is not null;
    }

    /// <summary>
    /// Waits until Character Select has finished loading: the selected name reads the same twice, half a second apart.
    /// Its Play button shows well before that, and clicks made then are taken late and together: on every fresh launch
    /// from 22 to 24 Nov the first portrait click entered the last played character, and the other portrait clicks landed
    /// in the world. False when the screen is gone. A name that never reads only costs the wait.
    /// </summary>
    private async Task<bool> WaitForCharacterSelectReadyAsync(CancellationToken cancellationToken)
    {
        if (_textLocator is null)
        {
            return true;
        }

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(_requireTemplateFilesOnDisk ? Math.Max(_uiWaitMs * 2, 20_000) : _uiWaitMs);
        string? lastName = null;
        var missing = 0;
        while (!IsPaused)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await IsCharacterSelectShownAsync(cancellationToken).ConfigureAwait(false))
            {
                if (++missing >= 3)
                {
                    return false;
                }
            }
            else
            {
                missing = 0;
                var name = await ReadSelectedCharacterNameAsync(cancellationToken).ConfigureAwait(false);
                if (name is not null && name == lastName)
                {
                    return true;
                }

                lastName = name;
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    _logger.LogInformation("Character Select: the selected name did not become readable; going on without it.");
                    await SaveDiagnosticFrameAsync("character-select-unread", cancellationToken).ConfigureAwait(false);
                    return true;
                }
            }

            await Task.Delay(_requireTemplateFilesOnDisk ? 500 : _pollMs, cancellationToken).ConfigureAwait(false);
        }

        return false;
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
        CharacterEntries++;
        // Play only, never Enter: the world can load before the key lands, and Enter there opens chat, so the next "F"
        // was typed into chat instead of opening the trader (seen live on 23 Nov).
        if (await IsCharacterSelectShownAsync(cancellationToken).ConfigureAwait(false))
        {
            await ClickCenterAsync(playBtn, cancellationToken).ConfigureAwait(false);
        }

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(_requireTemplateFilesOnDisk ? Math.Max(verifyDelayMs, 5_000) : verifyDelayMs);
        TemplateMatch? stillCharSelect;
        do
        {
            await Task.Delay(Math.Max(_pollMs, Math.Min(150, Math.Max(10, _settleMs / 4))), cancellationToken).ConfigureAwait(false);
            var frame = await _screenshots.CaptureAsync(CaptureRegion.FullScreen1024x768, cancellationToken).ConfigureAwait(false);
            stillCharSelect = await FindCharacterSelectPlayAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        while (stillCharSelect is not null && DateTimeOffset.UtcNow < deadline);

        if (stillCharSelect is not null)
        {
            await _input.DoubleClickAsync(portraitX, portraitY, cancellationToken).ConfigureAwait(false);
        }

        await WaitForInWorldAsync(cancellationToken).ConfigureAwait(false);
        _worldSeen = !await IsCharacterSelectShownAsync(cancellationToken).ConfigureAwait(false);
    }
}
