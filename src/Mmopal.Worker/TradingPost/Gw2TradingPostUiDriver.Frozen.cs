using Microsoft.Extensions.Logging;

namespace Mmopal.Worker;

/// <summary>
/// Noticing a Trading Post that no longer answers. On 24 Nov it stuck on one search for 38 minutes: 25 item windows did
/// not open, 25 cancel searches found nothing, and only the scheduled character swap ended it. Its picture stayed on
/// screen, so every "is the Trading Post open" check passed.
/// </summary>
public sealed partial class Gw2TradingPostUiDriver
{
    internal const int FailuresBeforeFrozen = 3;
    private int _failuresInARow;
    private DateTimeOffset? _firstFailureUtc;

    /// <summary>Several different things in a row did nothing on screen.</summary>
    public bool LooksFrozen => _failuresInARow >= FailuresBeforeFrozen;

    /// <summary>Something on screen that should have answered did not.</summary>
    private async Task NoteNoAnswerAsync(CancellationToken cancellationToken)
    {
        _firstFailureUtc ??= _utcNow();
        if (++_failuresInARow == FailuresBeforeFrozen)
        {
            await SaveDiagnosticFrameAsync("tp-frozen", cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>An item window opened or a cancel went through: the Trading Post answers.</summary>
    private void NoteAnswer()
    {
        _failuresInARow = 0;
        _firstFailureUtc = null;
    }

    /// <summary>
    /// After the Trading Post was reopened. What was learned from its silence (names that "cannot be read", cancels given
    /// up) is dropped, since it says nothing about those items.
    /// </summary>
    public void ForgetFrozen()
    {
        if (_firstFailureUtc is { } since)
        {
            foreach (var key in _unconfirmedCancelSearches.Where(entry => entry.Value.At >= since).Select(entry => entry.Key).ToArray())
            {
                _unconfirmedCancelSearches.Remove(key);
                _cancelGiveUps.Remove(key);
            }

            foreach (var key in _searchConfirmStats.Where(entry => entry.Value.Seen == 0).Select(entry => entry.Key).ToArray())
            {
                _searchConfirmStats.Remove(key);
            }
        }

        NoteAnswer();
    }

    /// <summary>Closes the Trading Post and opens it again. Fails when it does not close: then only leaving the character helps.</summary>
    public async Task<(bool Reopened, string Message)> ReopenFrozenTradingPostAsync(CancellationToken cancellationToken = default)
    {
        await CloseAllTpWindowsAsync(cancellationToken).ConfigureAwait(false);
        await SettleAsync(cancellationToken).ConfigureAwait(false);
        if (await IsTradingPostOpenAndActiveAsync(cancellationToken).ConfigureAwait(false))
        {
            return (false, "The Trading Post did not close.");
        }

        var (ready, message) = await EnsureTradingPostOpenAsync(cancellationToken).ConfigureAwait(false);
        if (ready)
        {
            ForgetFrozen();
        }

        return (ready, message);
    }
}
