using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Mmopal.Contracts;
using Mmopal.Gw2;
using Mmopal.Infrastructure;
using Mmopal.Native;
using Mmopal.Worker;
using Xunit;

namespace Mmopal.Domain.Tests;

public sealed partial class LiveTradingExecutorTests
{
    [Fact]
    public void DetectLauncherWhiteCard_HandlesDarkBottomLws4AndPofThemesWithWarmCardSurround()
    {
        const int width = 1024;
        const int height = 768;
        const int stride = width * 4;
        var pixels = new byte[stride * height];

        // Paint top white card (Y=180..444, X=10..980) and warm sunset LWS4 card area (Y=445..580, X=10..480)
        // with dark purple mountain artwork below Y=580 so raw white cardBottom stops at 444.
        for (var y = 180; y <= 444; y++)
        {
            for (var x = 10; x <= 980; x++)
            {
                var i = y * stride + x * 4;
                pixels[i] = 248; pixels[i + 1] = 248; pixels[i + 2] = 252; pixels[i + 3] = 255;
            }
        }

        for (var y = 445; y <= 580; y++)
        {
            for (var x = 10; x <= 480; x++)
            {
                var i = y * stride + x * 4;
                // Warm sunset background from LWS4 screenshot: R=228, G=188, B=168
                pixels[i] = 168; pixels[i + 1] = 188; pixels[i + 2] = 228; pixels[i + 3] = 255;
            }
        }

        // Paint orange LOG IN button at fixed HTML offset (X=48..172, Y=519..552)
        for (var y = 519; y <= 552; y++)
        {
            for (var x = 48; x <= 172; x++)
            {
                var i = y * stride + x * 4;
                pixels[i] = 30; pixels[i + 1] = 75; pixels[i + 2] = 155; pixels[i + 3] = 255;
            }
        }

        var frame = new AutomationFrame(DateTimeOffset.UtcNow, CaptureRegion.FullScreen1024x768, pixels, "Bgra32", stride);
        var state = Gw2TradingPostUiDriver.DetectLauncherWhiteCard(frame);

        Assert.NotNull(state);
        Assert.True(state!.LoginButtonActive);
        Assert.False(state.PlayButtonActive);
        Assert.InRange(state.LoginX, 95, 130);
        Assert.InRange(state.LoginY, 520, 550);
    }

    private sealed class FakeUiTextLocator(Func<CaptureRegion, IReadOnlyList<OcrTextBox>> responder) : IUiTextLocator
    {
        public Task<IReadOnlyList<OcrTextBox>> LocateLinesAsync(
            AutomationFrame frame,
            CaptureRegion region,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responder(region));
        }
    }

    private sealed class RestartTrackingPreparer : IGw2ClientPreparer
    {
        public int RestartCount { get; private set; }

        public Task<Gw2ClientPrepareResult> PrepareAsync(
            Gw2ClientSettings settings,
            bool allowLaunch,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new Gw2ClientPrepareResult(true, false, true, "Ready"));

        public Task<Gw2ClientPrepareResult> RestartAsync(
            Gw2ClientSettings settings,
            CancellationToken cancellationToken = default)
        {
            RestartCount++;
            return Task.FromResult(new Gw2ClientPrepareResult(true, true, false, "Restarted"));
        }
    }

    private sealed class FakeInputController : IInputController
    {
        public List<(int X, int Y)> Clicks { get; } = [];
        public List<(int X, int Y)> DoubleClicks { get; } = [];
        public List<string> KeyPresses { get; } = [];
        public List<string> TextsSent { get; } = [];

        public Task ClickAsync(int x, int y, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Clicks.Add((x, y));
            return Task.CompletedTask;
        }

        public Task DoubleClickAsync(int x, int y, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DoubleClicks.Add((x, y));
            Clicks.Add((x, y));
            return Task.CompletedTask;
        }

        public Task KeyPressAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            KeyPresses.Add(key);
            return Task.CompletedTask;
        }

        public Task SendTextAsync(string text, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TextsSent.Add(text);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeScreenshotProvider : IScreenshotProvider
    {
        public Func<CaptureRegion, AutomationFrame>? CustomFrameFactory { get; init; }

        public Task<AutomationFrame> CaptureAsync(CaptureRegion region, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CustomFrameFactory is not null)
            {
                return Task.FromResult(CustomFrameFactory(region));
            }

            return Task.FromResult(new AutomationFrame(DateTimeOffset.UtcNow, region, Array.Empty<byte>(), "Bgra32", 0));
        }
    }

    private sealed class FakeTemplateMatcher : ITemplateMatcher
    {
        private readonly Dictionary<string, TemplateMatch> _matches;

        public FakeTemplateMatcher(Dictionary<string, TemplateMatch>? matches = null)
        {
            _matches = matches ?? new Dictionary<string, TemplateMatch>(StringComparer.OrdinalIgnoreCase);
        }

        public List<(string Name, CaptureRegion? Region)> Queries { get; } = [];
        public Dictionary<string, Func<TemplateMatch?>> DynamicMatches { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Func<TemplateDefinition, TemplateMatch?>> PredicateMatches { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<TemplateMatch?> FindAsync(AutomationFrame frame, TemplateDefinition template, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Queries.Add((template.Name, template.Region));
            if (PredicateMatches.TryGetValue(template.Name, out var predicate))
            {
                return Task.FromResult(predicate(template));
            }

            if (DynamicMatches.TryGetValue(template.Name, out var factory))
            {
                return Task.FromResult(factory());
            }

            return Task.FromResult(_matches.TryGetValue(template.Name, out var match) ? match : null);
        }
    }

    private sealed class FakeWindowController : IWindowController
    {
        public Task<bool> EnsureWindowAsync(string processName, int width, int height, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(true);
        }
    }
}
