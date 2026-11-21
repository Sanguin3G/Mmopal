using Microsoft.Extensions.Logging;
using Mmopal.Contracts;
using Mmopal.Gw2;
using Mmopal.Native;
using Mmopal.Worker.Vision;

namespace Mmopal.Worker;

/// <summary>Parked characters: AFK and recovery swaps, slot choice, reading the selected name, logout at the session cap.</summary>
public sealed partial class Gw2TradingPostUiDriver
{
    public async Task<(bool Succeeded, string Message)> RotateParkedCharacterAsync(
        bool randomize,
        int parkedCharacterCount,
        CancellationToken cancellationToken = default)
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

        var slotOffset = PickParkedSlotOffset(randomize, parkedCharacterCount);
        _pendingSlot = (slotOffset, _utcNow());
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

        var who = selectedName is null ? $"slot #{slotOffset + 1}" : $"'{selectedName}' (slot #{slotOffset + 1})";
        CurrentCharacterLabel = who;
        var (reopened, openMsg) = await EnsureTradingPostOpenAsync(cancellationToken).ConfigureAwait(false);
        return reopened
            ? (true, $"Swapped to parked character {who} and reopened Trading Post.")
            : (false, $"Swapped to parked character {who}, but could not reopen Trading Post: {openMsg}");
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
}
