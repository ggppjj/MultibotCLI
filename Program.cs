using System.Text.Json.Serialization;
using LibMultibot.Helper_Classes;
using LibMultibot.Interfaces;
using Microsoft.Extensions.Configuration;
using MultibotCLI.Bots;
using Serilog;

_ = args;

List<IBot> bots = [];

CancellationTokenSource shutdownCts = new();
CancellationToken cancellationToken = shutdownCts.Token;

ConfigHelper.EnsureConfigDirectoriesExist();
if (!File.Exists(ConfigHelper.ProgramConfigPath))
{
    var defaultConfig = new ProgramConfig { LogLevel = "Information" };
    var json = System.Text.Json.JsonSerializer.Serialize(
        defaultConfig,
        ProgramConfigJsonContext.Default.ProgramConfig
    );
    File.WriteAllText(ConfigHelper.ProgramConfigPath, json);
}

var localApplicationConfig = new ConfigurationBuilder()
    .SetBasePath(Path.GetDirectoryName(ConfigHelper.ProgramConfigPath)!)
    .AddJsonFile(
        Path.GetFileName(ConfigHelper.ProgramConfigPath),
        optional: true,
        reloadOnChange: true
    )
    .Build();

var logController = new LogController();
var logger = LogController.SetupLogging(typeof(Program), localApplicationConfig);

// Lazy so that Ctrl+C, process exit and the normal exit path all share one shutdown.
var shutdown = new Lazy<Task>(RunShutdownAsync);

logger.Information("Starting...");

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    logger.Information("Received shutdown signal...");
    _ = shutdown.Value;
};
AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    logger.Information("Application process exiting...");
    // The process ends when this handler returns, so a SIGTERM has to wait here.
    shutdown.Value.GetAwaiter().GetResult();
};

try
{
    AddBot("TCHJR", () => new TCHJRBot(shutdownCts));
    AddBot("Crunchybot", () => new CrunchyBot(shutdownCts));
    var initResults = await Task.WhenAll(bots.Select(InitBotAsync));
    var runningBots = bots.Where((_, i) => initResults[i]).ToList();
    if (runningBots.Count == 0)
    {
        logger.Fatal("All bots failed to initialize.");
        Environment.ExitCode = 1;
        shutdownCts.Cancel();
    }
    else
    {
        logger.Information("Started {Running} of {Total} bot(s).", runningBots.Count, bots.Count);
        // A bot that fails or is shut down later stops only itself. Exit once none are left.
        _ = Task.WhenAll(runningBots.Select(b => b.Stopped))
            .ContinueWith(
                stopped =>
                {
                    if (stopped.Result.Any(failed => failed))
                    {
                        logger.Fatal("No bots left running, and at least one failed.");
                        Environment.ExitCode = 1;
                    }
                    else
                        logger.Information("All bots were shut down.");
                    shutdownCts.Cancel();
                },
                TaskScheduler.Default
            );
    }
    await Task.Delay(Timeout.Infinite, shutdownCts.Token);
}
catch (OperationCanceledException) { }
catch (Exception ex)
{
    logger.Fatal(ex, "Fatal error occurred during application execution.");
    Environment.ExitCode = 1;
}
finally
{
    await shutdown.Value;
}

// One bot failing to construct or start must not take the others down with it.
void AddBot(string name, Func<IBot> create)
{
    try
    {
        bots.Add(create());
    }
    catch (Exception ex)
    {
        logger.Error(ex, "Bot '{Bot}' could not be created. Continuing without it.", name);
    }
}

async Task<bool> InitBotAsync(IBot bot)
{
    try
    {
        return await bot.Init();
    }
    catch (Exception ex)
    {
        logger.Error(ex, "Bot '{Bot}' failed to initialize. Continuing without it.", bot.Name);
        return false;
    }
}

async Task ShutdownBotAsync(IBot bot)
{
    try
    {
        await bot.Shutdown();
    }
    catch (Exception ex)
    {
        logger.Error(ex, "Error shutting down bot '{Bot}'.", bot.Name);
        Environment.ExitCode = 1;
    }
}

async Task RunShutdownAsync()
{
    // Cancelling below can resume the main thread inline, which then reads shutdown.Value.
    // Lazy throws if that happens while this factory is still running, so return first.
    await Task.Yield();
    try
    {
        logger.Information("Starting shutdown process...");
        await Task.WhenAll(bots.Select(ShutdownBotAsync));
        logger.Information("Application shutdown completed.");
    }
    catch (Exception ex)
    {
        logger.Error(ex, "Error during shutdown.");
        Environment.ExitCode = 1;
    }
    finally
    {
        // Always release the main thread, or a failed shutdown would hang the process.
        shutdownCts.Cancel();
        Log.CloseAndFlush();
    }
}

public class ProgramConfig
{
    public string LogLevel { get; set; } = "Information";
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ProgramConfig))]
internal partial class ProgramConfigJsonContext : JsonSerializerContext { }
