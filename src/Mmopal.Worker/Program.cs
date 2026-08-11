using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Mmopal.Licensing;
using Mmopal.Infrastructure;

namespace Mmopal.Worker;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        var pipeName = GetArgument(args, "--pipe") ?? "Mmopal.Worker";
        var dataRoot = GetArgument(args, "--data-root");
        var paths = RuntimePaths.Create(dataRoot);
        paths.EnsureDirectories();

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("config/appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables("MMOPAL_")
            .Build();

        var licensingOptions = configuration.GetSection("Licensing").Get<LicensingOptions>() ?? new LicensingOptions();
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

        var runtime = new WorkerRuntime(store, logger);
        var server = new NamedPipeWorkerServer(pipeName, runtime, logger);
        await server.RunAsync();

        await runtime.StopAsync(CancellationToken.None);
    }

    private static string? GetArgument(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        }

        return null;
    }
}
