namespace Mmopal.Contracts;

public interface IScreenshotProvider
{
    Task<AutomationFrame> CaptureAsync(CaptureRegion region, CancellationToken cancellationToken = default);
}

public interface ITemplateMatcher
{
    Task<TemplateMatch?> FindAsync(AutomationFrame frame, TemplateDefinition template, CancellationToken cancellationToken = default);
}

public interface IOcrReader
{
    Task<OcrResult?> ReadAsync(AutomationFrame frame, CaptureRegion region, CancellationToken cancellationToken = default);
}

public sealed record OcrTextBox(string Text, int X, int Y, int Width, int Height);

public interface IUiTextLocator
{
    Task<IReadOnlyList<OcrTextBox>> LocateLinesAsync(AutomationFrame frame, CaptureRegion region, CancellationToken cancellationToken = default);
}

public interface IInputController
{
    Task ClickAsync(int x, int y, CancellationToken cancellationToken = default);
    async Task DoubleClickAsync(int x, int y, CancellationToken cancellationToken = default)
    {
        await ClickAsync(x, y, cancellationToken).ConfigureAwait(false);
        await Task.Delay(70, cancellationToken).ConfigureAwait(false);
        await ClickAsync(x, y, cancellationToken).ConfigureAwait(false);
    }
    /// <summary>Moves the cursor without clicking (hover). Inputs that cannot move do nothing.</summary>
    Task MoveAsync(int x, int y, CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task KeyPressAsync(string key, CancellationToken cancellationToken = default);
    Task SendTextAsync(string text, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

public interface IGw2DataProvider
{
    Task<IReadOnlyList<Transaction>> GetTransactionsAsync(CancellationToken cancellationToken = default);
    Task<AccountBalance?> GetBalanceAsync(CancellationToken cancellationToken = default);
}

public sealed record CaptureRegion(int X, int Y, int Width, int Height)
{
    public static CaptureRegion FullScreen1024x768 => new(0, 0, 1024, 768);
}

public sealed record TemplateDefinition(string Name, string FilePath, CaptureRegion? Region = null, decimal MatchScore = 0.8m);

public sealed record AutomationFrame(
    DateTimeOffset CapturedAt,
    CaptureRegion Region,
    byte[] Pixels,
    string PixelFormat = "Bgra32",
    int Stride = 0);

public sealed record TemplateMatch(int X, int Y, int Width, int Height, decimal Score);

public sealed record OcrResult(string Text, decimal Confidence);
