using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Mmopal.Contracts;
using Mmopal.Gw2;
using Mmopal.Infrastructure;
using Mmopal.Native;
using Mmopal.Worker;

namespace Mmopal.Domain.Tests;

public sealed class Gw2ClientControllerTests
{
    [Fact]
    public void Installation_locator_finds_standalone_and_steam_installs()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Program Files\Guild Wars 2\Gw2-64.exe",
            @"D:\SteamLibrary\steamapps\common\Guild Wars 2\Gw2-64.exe"
        };

        var found = Gw2InstallationLocator.FindInstalledPaths(
            [@"C:\", @"D:\"],
            path => existing.Contains(path));

        Assert.Equal(2, found.Count);
        Assert.Contains(@"C:\Program Files\Guild Wars 2\Gw2-64.exe", found);
        Assert.Contains(@"D:\SteamLibrary\steamapps\common\Guild Wars 2\Gw2-64.exe", found);
    }

    [Fact]
    public void Process_controller_normalizes_exe_names_and_detects_steam_uri()
    {
        Assert.Equal("Gw2-64", Win32ProcessController.NormalizeProcessName("Gw2-64.exe"));
        Assert.Equal("Gw2-64", Win32ProcessController.NormalizeProcessName(@"C:\Games\Guild Wars 2\Gw2-64.exe"));
        Assert.Equal("Gw2-64", Win32ProcessController.NormalizeProcessName("Gw2-64"));
        Assert.True(Win32ProcessController.IsUriLaunch("steam://rungameid/1284210"));
        Assert.False(Win32ProcessController.IsUriLaunch(@"C:\Program Files\Guild Wars 2\Gw2-64.exe"));
    }

    [Fact]
    public void Window_controller_prefers_gw2_render_class_over_launcher_or_title_match()
    {
        var candidates = new[]
        {
            new Win32WindowController.WindowCandidate(new IntPtr(10), 100, "Guild Wars 2", "Chrome_WidgetWin_1", MatchesProcess: false),
            new Win32WindowController.WindowCandidate(new IntPtr(20), 200, "Guild Wars 2", "ArenaNet_Launcher", MatchesProcess: true),
            new Win32WindowController.WindowCandidate(new IntPtr(30), 200, "Guild Wars 2", "ArenaNet_Gr_Window_Class", MatchesProcess: true)
        };

        var selected = Win32WindowController.SelectBestWindow(candidates, "Guild Wars 2");
        Assert.Equal(new IntPtr(30), selected);
    }

    [Fact]
    public void Window_controller_ignores_small_downloading_splash_and_selects_full_launcher_window()
    {
        var onlySplash = new[]
        {
            new Win32WindowController.WindowCandidate(new IntPtr(11), 200, "Guild Wars 2", "ArenaNet", MatchesProcess: true, Width: 690, Height: 420)
        };
        Assert.Equal(IntPtr.Zero, Win32WindowController.SelectBestWindow(onlySplash, "Guild Wars 2"));

        var splashAndMainLauncher = new[]
        {
            new Win32WindowController.WindowCandidate(new IntPtr(11), 200, "Guild Wars 2", "ArenaNet", MatchesProcess: true, Width: 690, Height: 420),
            new Win32WindowController.WindowCandidate(new IntPtr(22), 200, "Guild Wars 2", "ArenaNet", MatchesProcess: true, Width: 1010, Height: 877)
        };
        Assert.Equal(new IntPtr(22), Win32WindowController.SelectBestWindow(splashAndMainLauncher, "Guild Wars 2"));
    }

    [Fact]
    public async Task Client_preparer_snaps_already_running_gw2_without_relaunching()
    {
        var processes = new FakeProcessController { Running = true };
        var windows = new FakeWindowController { SucceedOnAttempt = 1 };
        var preparer = new Gw2ClientPreparer(processes, windows, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(5));

        var result = await preparer.PrepareAsync(new Gw2ClientSettings(WindowWidth: 1024, WindowHeight: 768), allowLaunch: true);

        Assert.True(result.WindowReady);
        Assert.True(result.AlreadyRunning);
        Assert.False(result.Launched);
        Assert.Empty(processes.Launches);
        Assert.Single(windows.Calls);
        Assert.Equal(("Guild Wars 2", 1024, 768), windows.Calls[0]);
    }

    [Fact]
    public async Task Client_preparer_launches_and_waits_for_window_when_allowed()
    {
        var processes = new FakeProcessController { Running = false };
        var windows = new FakeWindowController { SucceedOnAttempt = 3 };
        var preparer = new Gw2ClientPreparer(processes, windows, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(5));
        var settings = new Gw2ClientSettings(
            ExecutablePath: @"C:\Games\Guild Wars 2\Gw2-64.exe",
            LaunchArguments: "-windowed",
            WindowWidth: 1024,
            WindowHeight: 768);

        var result = await preparer.PrepareAsync(settings, allowLaunch: true);

        Assert.True(result.WindowReady);
        Assert.True(result.Launched);
        Assert.False(result.AlreadyRunning);
        Assert.Single(processes.Launches);
        Assert.Equal((@"C:\Games\Guild Wars 2\Gw2-64.exe", "-windowed"), processes.Launches[0]);
        Assert.Equal(3, windows.Calls.Count);
    }

    [Fact]
    public async Task Client_preparer_skips_launch_when_auto_launch_is_disabled()
    {
        var processes = new FakeProcessController { Running = false };
        var windows = new FakeWindowController { SucceedOnAttempt = 1 };
        var preparer = new Gw2ClientPreparer(processes, windows, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(5));

        var result = await preparer.PrepareAsync(new Gw2ClientSettings(ExecutablePath: @"C:\Games\Gw2-64.exe"), allowLaunch: false);

        Assert.False(result.WindowReady);
        Assert.False(result.Launched);
        Assert.Empty(processes.Launches);
        Assert.Empty(windows.Calls);
    }

    [Fact]
    public void Client_settings_parse_swap_hours_and_round_trip_to_disk()
    {
        Assert.True(Gw2ClientSettings.TryParseSwapHours("", out var blank, out _));
        Assert.Null(blank);

        Assert.True(Gw2ClientSettings.TryParseSwapHours("2.5", out var twoHalfHours, out _));
        Assert.Equal(150, twoHalfHours);

        Assert.False(Gw2ClientSettings.TryParseSwapHours("-1", out _, out var negativeError));
        Assert.NotNull(negativeError);

        Assert.False(Gw2ClientSettings.TryParseSwapHours("48", out _, out var tooLargeError));
        Assert.NotNull(tooLargeError);

        var tempDir = Path.Combine(Path.GetTempPath(), "mmopal-client-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(tempDir, "client-settings.json");
            var original = new Gw2ClientSettings(
                ExecutablePath: @"D:\Games\GW2\Gw2-64.exe",
                AutoPrepareOnScreenStart: true,
                AutoLaunchIfNotRunning: true,
                SwapCharacterIntervalMinutes: 120,
                SwapCharacterJitterMinutes: 15,
                RandomizeCharacterSelection: true);
            Gw2ClientSettingsFile.Save(path, original);

            var loaded = Gw2ClientSettingsFile.LoadOrDefault(path);
            Assert.Equal(original, loaded);
            Assert.Equal("2", loaded.FormatSwapHours());
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Afk_character_swap_schedule_respects_interval_jitter_and_advances()
    {
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var disabled = new AfkCharacterSwapSchedule(null, startedAt: start);
        Assert.False(disabled.IsEnabled);
        Assert.False(disabled.IsDue(start.AddHours(10)));

        var schedule = new AfkCharacterSwapSchedule(
            intervalMinutes: 120,
            jitterMinutes: 15,
            randomizeCharacter: true,
            startedAt: start,
            nextRandom: (min, max) =>
            {
                Assert.Equal(-15, min);
                Assert.Equal(16, max);
                return -10;
            });

        Assert.True(schedule.IsEnabled);
        Assert.Equal(start.AddMinutes(110), schedule.NextSwapAt);
        Assert.False(schedule.IsDue(start.AddMinutes(109)));
        Assert.True(schedule.IsDue(start.AddMinutes(110)));

        schedule.MarkSwapped(start.AddMinutes(110));
        Assert.Equal(1, schedule.SwapCount);
        Assert.Equal(start.AddMinutes(220), schedule.NextSwapAt);
    }

    [Fact]
    public async Task Worker_reports_afk_character_swap_schedule_in_health_events()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "mmopal-worker-afk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var store = new CheckpointStore(Path.Combine(tempRoot, "mmopal.db"));
            await store.InitializeAsync();
            var runtime = new WorkerRuntime(store, NullLogger.Instance);
            var healthReady = new TaskCompletionSource<WorkerEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            runtime.EventRaised += (_, workerEvent) =>
            {
                if (workerEvent.Type == WorkerEventTypes.Health)
                {
                    healthReady.TrySetResult(workerEvent);
                }
            };

            var payload = JsonSerializer.Serialize(new TradingReplayRequest(
                Source: "replay",
                ClientSettings: new Gw2ClientSettings(
                    SwapCharacterIntervalMinutes: 120,
                    RandomizeCharacterSelection: true)),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            await runtime.HandleAsync(new WorkerCommand(WorkerCommandTypes.Start, "req-afk", BotKind.TradingPost, RunMode.DryRun, "balanced", payload));
            var health = await healthReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await runtime.HandleAsync(new WorkerCommand(WorkerCommandTypes.Stop));

            using var document = JsonDocument.Parse(health.Payload);
            var swapText = document.RootElement.GetProperty("characterSwap").GetString();
            Assert.NotNull(swapText);
            Assert.Contains("Next AFK swap (random character)", swapText, StringComparison.Ordinal);
            Assert.False(document.RootElement.GetProperty("characterSwapDue").GetBoolean());
            Assert.NotEqual(JsonValueKind.Null, document.RootElement.GetProperty("nextCharacterSwapAt").ValueKind);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Installation_locator_parses_steam_vdf_and_epic_manifest()
    {
        const string vdf = """
            "libraryfolders"
            {
                "0"
                {
                    "path"		"C:\\Program Files (x86)\\Steam"
                }
                "1"		"D:\\SteamLibrary"
            }
            """;

        var libs = Gw2InstallationLocator.ParseSteamLibraryFolders(vdf);
        Assert.Equal(2, libs.Count);
        Assert.Contains(@"C:\Program Files (x86)\Steam", libs);
        Assert.Contains(@"D:\SteamLibrary", libs);

        const string epicGw2 = """{"DisplayName":"Guild Wars 2","InstallLocation":"E:\\EpicGames\\GuildWars2","LaunchExecutable":"Gw2-64.exe"}""";
        Assert.Equal(@"E:\EpicGames\GuildWars2\Gw2-64.exe", Gw2InstallationLocator.ParseEpicManifestInstallLocation(epicGw2));

        const string epicOther = """{"DisplayName":"Other Game","InstallLocation":"E:\\EpicGames\\Other"}""";
        Assert.Null(Gw2InstallationLocator.ParseEpicManifestInstallLocation(epicOther));
    }

    [Fact]
    public async Task Client_preparer_respects_bounded_launch_attempts_and_does_not_loop()
    {
        var processes = new FakeProcessController { Running = false };
        var windows = new FakeWindowController { SucceedOnAttempt = 999 };
        var preparer = new Gw2ClientPreparer(processes, windows, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(5));
        var settings = new Gw2ClientSettings(
            ExecutablePath: @"C:\Games\Guild Wars 2\Gw2-64.exe",
            MaxLaunchAttempts: 2);

        var result = await preparer.PrepareAsync(settings, allowLaunch: true);

        Assert.False(result.WindowReady);
        Assert.True(result.Launched);
        Assert.Equal(2, processes.Launches.Count);
        Assert.Contains("2 launch attempt(s)", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Worker_halts_cleanly_into_needs_attention_when_screen_gw2_window_unavailable()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "mmopal-worker-halt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var store = new CheckpointStore(Path.Combine(tempRoot, "mmopal.db"));
            await store.InitializeAsync();
            var preparer = new StubFailingPreparer();
            var runtime = new WorkerRuntime(store, NullLogger.Instance, new StubMarketDataProvider(), clientPreparer: preparer);

            var attentionReady = new TaskCompletionSource<WorkerStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            runtime.EventRaised += (_, workerEvent) =>
            {
                if (workerEvent.Type == WorkerEventTypes.Status)
                {
                    var s = JsonSerializer.Deserialize<WorkerStatus>(workerEvent.Payload);
                    if (s?.State == WorkerState.NeedsAttention)
                    {
                        attentionReady.TrySetResult(s);
                    }
                }
            };

            var payload = JsonSerializer.Serialize(new TradingReplayRequest(
                Source: "screen",
                ClientSettings: new Gw2ClientSettings(AutoPrepareOnScreenStart: true)),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            await runtime.HandleAsync(new WorkerCommand(WorkerCommandTypes.Start, "req-halt", BotKind.TradingPost, RunMode.Observe, "balanced", payload));
            var status = await attentionReady.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(WorkerState.NeedsAttention, status.State);
            Assert.Equal("GW2 window unavailable", status.LastAction);
            Assert.Contains("2 launch attempt(s)", status.Message, StringComparison.Ordinal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Worker_stops_cleanly_when_max_session_duration_cap_expires()
    {
        var start = new DateTimeOffset(2026, 11, 19, 23, 0, 0, TimeSpan.Zero);
        var cap = new SessionDurationCap(durationMinutes: 600, startedAt: start);
        Assert.True(cap.IsEnabled);
        Assert.False(cap.IsExpired(start.AddMinutes(599)));
        Assert.True(cap.IsExpired(start.AddMinutes(600)));

        var tempRoot = Path.Combine(Path.GetTempPath(), "mmopal-worker-cap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var store = new CheckpointStore(Path.Combine(tempRoot, "mmopal.db"));
            await store.InitializeAsync();
            var calls = 0;
            DateTimeOffset Clock() => Interlocked.Increment(ref calls) == 1 ? start : start.AddHours(10).AddMinutes(1);

            var runtime = new WorkerRuntime(store, NullLogger.Instance, utcNow: Clock);
            var stoppedReady = new TaskCompletionSource<WorkerStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            runtime.EventRaised += (_, workerEvent) =>
            {
                if (workerEvent.Type == WorkerEventTypes.Status)
                {
                    var s = JsonSerializer.Deserialize<WorkerStatus>(workerEvent.Payload);
                    if (s?.State == WorkerState.Stopped && s.LastAction == "Session cap reached")
                    {
                        stoppedReady.TrySetResult(s);
                    }
                }
            };

            var payload = JsonSerializer.Serialize(new TradingReplayRequest(
                Source: "replay",
                ClientSettings: new Gw2ClientSettings(MaxSessionDurationMinutes: 600)),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            await runtime.HandleAsync(new WorkerCommand(WorkerCommandTypes.Start, "req-cap", BotKind.TradingPost, RunMode.DryRun, "balanced", payload));
            var status = await stoppedReady.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(WorkerState.Stopped, status.State);
            Assert.Equal("Session cap reached", status.LastAction);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Installation_locator_rejects_non_executable_icons_and_accepts_exe()
    {
        Assert.True(Gw2InstallationLocator.IsValidExecutableFilePath(@"D:\Games\Guild Wars 2\Gw2-64.exe"));
        Assert.False(Gw2InstallationLocator.IsValidExecutableFilePath(@"D:\Steam\steam\games\icon.ico"));
        Assert.False(Gw2InstallationLocator.IsValidExecutableFilePath(@"C:\Users\Public\Desktop\GW2.lnk"));
        Assert.False(Gw2InstallationLocator.IsValidExecutableFilePath(""));
        Assert.False(Gw2InstallationLocator.IsValidExecutableFilePath(null));
    }

    [Fact]
    public void Latency_presets_and_client_settings_file_roundtrip_high_ping_values()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mmopal-latency-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var highPing = Gw2LatencyPresets.FindById("HighPing");
            Assert.NotNull(highPing);
            Assert.Equal(7500, highPing!.UiLoadWaitMs);
            Assert.Equal(900, highPing.ActionSettleDelayMs);
            Assert.Equal(60, highPing.ApiTimeoutSeconds);

            var path = Path.Combine(tempDir, "client-settings.json");
            var settings = new Gw2ClientSettings(
                ExecutablePath: @"C:\Games\Guild Wars 2\Gw2-64.exe",
                LatencyProfile: "Custom",
                UiLoadWaitMs: 8200,
                ActionSettleDelayMs: 950,
                ApiTimeoutSeconds: 75);
            Gw2ClientSettingsFile.Save(path, settings);
            var loaded = Gw2ClientSettingsFile.LoadOrDefault(path);

            Assert.Equal("Custom", loaded.LatencyProfile);
            Assert.Equal(8200, loaded.UiLoadWaitMs);
            Assert.Equal(950, loaded.ActionSettleDelayMs);
            Assert.Equal(75, loaded.ApiTimeoutSeconds);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Client_preparer_launches_steam_uri_with_null_args_and_restarts_cleanly()
    {
        var arenaSettings = new Gw2ClientSettings(ExecutablePath: @"C:\Games\Guild Wars 2\Gw2-64.exe", LaunchPlatform: Gw2ClientSettings.PlatformArenaNet);
        Assert.False(arenaSettings.IsSteamPlatform);
        Assert.Equal(@"C:\Games\Guild Wars 2\Gw2-64.exe", arenaSettings.ResolveEffectiveLaunchTarget());

        var steamSettings = new Gw2ClientSettings(ExecutablePath: @"C:\Games\Guild Wars 2\Gw2-64.exe", LaunchPlatform: Gw2ClientSettings.PlatformSteam);
        Assert.True(steamSettings.IsSteamPlatform);
        Assert.Equal(Gw2ClientSettings.SteamLaunchUri, steamSettings.ResolveEffectiveLaunchTarget());

        var processes = new FakeProcessController { Running = false };
        var windows = new FakeWindowController { SucceedOnAttempt = 1 };
        var preparer = new Gw2ClientPreparer(processes, windows, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(5));

        var steamResult = await preparer.PrepareAsync(steamSettings, allowLaunch: true);
        Assert.True(steamResult.WindowReady);
        Assert.Contains("via Steam", steamResult.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(processes.Launches);
        Assert.Equal((Gw2ClientSettings.SteamLaunchUri, null), processes.Launches[0]);

        var restartResult = await preparer.RestartAsync(arenaSettings);
        Assert.True(restartResult.WindowReady);
        Assert.True(restartResult.Launched);
        Assert.Equal(2, processes.Launches.Count);
        Assert.Equal((@"C:\Games\Guild Wars 2\Gw2-64.exe", "-autologin -windowed"), processes.Launches[1]);
    }

    [Fact]
    public void Pace_profiles_are_tuned_monotonically_for_profit_and_safety()
    {
        var loader = new ProfileLoader();
        var safe = loader.Load("safe", AppContext.BaseDirectory);
        var balanced = loader.Load("balanced", AppContext.BaseDirectory);
        var fast = loader.Load("fast", AppContext.BaseDirectory);

        Assert.True(safe.OverbidScanIntervalSeconds > balanced.OverbidScanIntervalSeconds);
        Assert.True(balanced.OverbidScanIntervalSeconds > fast.OverbidScanIntervalSeconds);
        Assert.True(safe.MaxOverbidUpdatesPerPass <= balanced.MaxOverbidUpdatesPerPass);
        Assert.True(balanced.MaxOverbidUpdatesPerPass < fast.MaxOverbidUpdatesPerPass);
        Assert.Equal(90, safe.OverbidScanIntervalSeconds);
        Assert.Equal(2, safe.MaxOverbidUpdatesPerPass);
        Assert.Equal(50, balanced.OverbidScanIntervalSeconds);
        Assert.Equal(3, balanced.MaxOverbidUpdatesPerPass);
        Assert.Equal(25, fast.OverbidScanIntervalSeconds);
        Assert.Equal(5, fast.MaxOverbidUpdatesPerPass);
    }

    private sealed class StubFailingPreparer : IGw2ClientPreparer
    {
        public Task<Gw2ClientPrepareResult> PrepareAsync(
            Gw2ClientSettings settings,
            bool allowLaunch,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new Gw2ClientPrepareResult(
                WindowReady: false,
                Launched: true,
                AlreadyRunning: false,
                Message: "Guild Wars 2 did not open a ready 1024x768 window after 2 launch attempt(s)."));
    }

    private sealed class StubMarketDataProvider : IGw2DataProvider
    {
        public Task<IReadOnlyList<Transaction>> GetTransactionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Transaction>>(Array.Empty<Transaction>());

        public Task<AccountBalance?> GetBalanceAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<AccountBalance?>(new AccountBalance(100_000));
    }

    private sealed class FakeProcessController : IProcessController
    {
        public bool Running { get; set; }
        public List<(string Path, string? Arguments)> Launches { get; } = [];

        public bool IsRunning(string executableName) => Running;

        public Task<bool> LaunchAsync(string executablePath, string? arguments, CancellationToken cancellationToken = default)
        {
            Launches.Add((executablePath, arguments));
            Running = true;
            return Task.FromResult(true);
        }

        public Task StopAsync(string executableName, CancellationToken cancellationToken = default)
        {
            Running = false;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeWindowController : IWindowController
    {
        public int SucceedOnAttempt { get; set; } = 1;
        public List<(string ProcessName, int Width, int Height)> Calls { get; } = [];

        public Task<bool> EnsureWindowAsync(string processName, int width, int height, CancellationToken cancellationToken = default)
        {
            Calls.Add((processName, width, height));
            return Task.FromResult(Calls.Count >= SucceedOnAttempt);
        }
    }
}
