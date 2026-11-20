using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Mmopal.Gw2;
using Mmopal.Infrastructure;
using Mmopal.Licensing;

namespace Mmopal.Worker;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        var pipeName = GetArgument(args, "--pipe") ?? "Mmopal.Worker";
        var dataRoot = GetArgument(args, "--data-root");
        var parentPidText = GetArgument(args, "--parent-pid");
        var paths = RuntimePaths.Create(dataRoot);
        paths.EnsureDirectories();

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("config/appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables("MMOPAL_")
            .Build();

        var licensingOptions = configuration.GetSection("Licensing").Get<LicensingOptions>() ?? new LicensingOptions();
        var nativeOptions = configuration.GetSection("Native").Get<NativeOptions>() ?? new NativeOptions();
        var licenseProvider = new LicenseProviderFactory().Create(licensingOptions);
        var licenseService = new LicenseService(licenseProvider, licensingOptions);

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddSimpleConsole(options => options.SingleLine = true);
            builder.AddProvider(new FileLoggerProvider(paths.Logs));
        });

        var logger = loggerFactory.CreateLogger("Mmopal.Worker");
        _ = licenseService.RefreshAsync().ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully)
            {
                logger.LogInformation("License provider {Provider}: enabled={Enabled}, valid={Valid}, message={Message}",
                    task.Result.Provider, task.Result.IsEnabled, task.Result.IsValid, task.Result.Message);
            }
            else if (task.Exception is not null)
            {
                logger.LogWarning(task.Exception.GetBaseException(), "License refresh failed; continuing without gating the worker.");
            }
        }, TaskScheduler.Default);

        var store = new CheckpointStore(paths.Database);
        await store.InitializeAsync();
        var ledgerStore = new TradingLedgerStore(paths.Database);
        await ledgerStore.InitializeAsync();
        var sessionReportStore = new SessionReportStore(paths.Database);
        await sessionReportStore.InitializeAsync();

        var templateRoot = ResolveConfiguredDirectory(nativeOptions.TemplateRoot);
        using var guildWars = new HttpClient { BaseAddress = new Uri("https://api.guildwars2.com/"), Timeout = TimeSpan.FromSeconds(120) };
        var runtime = new WorkerRuntime(
            store,
            logger,
            new Gw2OfficialDataProvider(guildWars, nativeOptions.Gw2ApiKey),
            templateRoot,
            ledgerStore: ledgerStore,
            sessionReportStore: sessionReportStore);

        using var shutdownCancellation = new CancellationTokenSource();
        Task? parentWatchdog = null;
        if (int.TryParse(parentPidText, out var parentPid) && parentPid > 0)
        {
            parentWatchdog = WatchParentProcessAsync(parentPid, shutdownCancellation, logger);
        }

        var server = new NamedPipeWorkerServer(pipeName, runtime, logger);
        await server.RunAsync(shutdownCancellation.Token);

        shutdownCancellation.Cancel();
        if (parentWatchdog is not null)
        {
            try { await parentWatchdog; } catch (OperationCanceledException) { }
        }

        await runtime.StopAsync(CancellationToken.None);
    }

    private static async Task WatchParentProcessAsync(
        int parentPid,
        CancellationTokenSource shutdownCancellation,
        ILogger logger)
    {
        try
        {
            using var parent = Process.GetProcessById(parentPid);
            await parent.WaitForExitAsync(shutdownCancellation.Token);
            logger.LogWarning("Parent desktop process {ParentPid} exited; shutting down worker.", parentPid);
            shutdownCancellation.Cancel();
        }
        catch (ArgumentException)
        {
            logger.LogWarning("Parent desktop process {ParentPid} is no longer running; shutting down worker.", parentPid);
            shutdownCancellation.Cancel();
        }
        catch (InvalidOperationException)
        {
            logger.LogWarning("Parent desktop process {ParentPid} has already exited; shutting down worker.", parentPid);
            shutdownCancellation.Cancel();
        }
        catch (OperationCanceledException) when (shutdownCancellation.IsCancellationRequested)
        {
        }
    }

    private static string? GetArgument(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        }

        return null;
    }

    private static string ResolveConfiguredDirectory(string value)
    {
        var expanded = Environment.ExpandEnvironmentVariables(value);
        if (Path.IsPathRooted(expanded)) return Path.GetFullPath(expanded);

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 8 && current is not null; depth++, current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, expanded);
            if (Directory.Exists(candidate)) return Path.GetFullPath(candidate);
        }

        return Path.GetFullPath(expanded, AppContext.BaseDirectory);
    }
}
