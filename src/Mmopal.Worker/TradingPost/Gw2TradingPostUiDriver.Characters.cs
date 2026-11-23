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

        var previousName = await ReadSelectedCharacterNameAsync(cancellationToken).ConfigureAwait(false);
        var named = preferredNames is { Count: > 0 }
            ? await FindPortraitByNameAsync(preferredNames, parkedCharacterCount, cancellationToken).ConfigureAwait(false)
            : null;
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
        CurrentCharacterName = selectedName;
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
            if (IsPaused)
            {
                return null;
            }

            await _input.ClickAsync(DeterministicCharSlotOnePoint.X + (offset * CharacterSlotStrideX), DeterministicCharSlotOnePoint.Y, cancellationToken).ConfigureAwait(false);
            await SettleShortAsync(cancellationToken).ConfigureAwait(false);
            var name = await ReadSelectedCharacterNameAsync(cancellationToken).ConfigureAwait(false);
            if (name is null)
            {
                // Right after a fresh launch Character Select is still fading in: no name was read on any portrait in
                // five launches on 22 Nov. One more read after a full settle.
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
            if (frame.Pixels.Length == 0)
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
    /// Closes any open Trading Post dialogs, opens the Game Menu, and performs an in-game Exit to Desktop
    /// or Log Out so character/TP state flushes cleanly before closing the game executable.
    /// </summary>
    public async Task<(bool LoggedOut, string Message)> GracefulLogoutAndCloseGameAsync(
        CancellationToken cancellationToken = default)
    {
        if (!await EnsureGameWindowAsync(cancellationToken).ConfigureAwait(false))
        {
            return (false, "Guild Wars 2 window not active for in-game logout.");
        }

        await CloseTpWindowsAndClickRestAsync(cancellationToken).ConfigureAwait(false);
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
                    var (x, y) = OcrBoxCenter(exitDesktop);
                    await _input.ClickAsync(x, y, cancellationToken).ConfigureAwait(false);
                    await WaitAfterLogoutClickAsync(cancellationToken).ConfigureAwait(false);
                    return (true, "Clicked 'Exit to Desktop' cleanly from the in-game menu.");
                }

                var ocrLogout = lines.FirstOrDefault(l =>
                    l.Text.Contains("Log Out", StringComparison.OrdinalIgnoreCase));
                if (ocrLogout is not null)
                {
                    var (x, y) = OcrBoxCenter(ocrLogout);
                    await _input.ClickAsync(x, y, cancellationToken).ConfigureAwait(false);
                    await SettleShortAsync(cancellationToken).ConfigureAwait(false);
                    await _input.KeyPressAsync("enter", cancellationToken).ConfigureAwait(false);
                    await WaitAfterLogoutClickAsync(cancellationToken).ConfigureAwait(false);
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
            await WaitAfterLogoutClickAsync(cancellationToken).ConfigureAwait(false);
            return (true, "Logged out cleanly before closing Guild Wars 2.");
        }

        return (false, "Game Menu logout button not detected; closing process directly.");
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

    private Task WaitAfterLogoutClickAsync(CancellationToken cancellationToken) =>
        Task.Delay(_requireTemplateFilesOnDisk ? 2000 : 20, cancellationToken);

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
        // Play only, never Enter: the world can load before the key lands, and Enter there opens chat, so the next "F"
        // was typed into chat instead of opening the trader (seen live on 23 Nov).
        await ClickCenterAsync(playBtn, cancellationToken).ConfigureAwait(false);
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
    }
}
