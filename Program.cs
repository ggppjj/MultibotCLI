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
    bots.Add(new TCHJRBot(shutdownCts));
    var initResults = await Task.WhenAll(bots.Select(b => b.Init()));
    if (initResults.All(r => !r))
    {
        logger.Fatal("All bots failed to initialize.");
        Environment.ExitCode = 1;
        shutdownCts.Cancel();
    }
    else
        logger.Information("Started.");
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

async Task RunShutdownAsync()
{
    // Cancelling below can resume the main thread inline, which then reads shutdown.Value.
    // Lazy throws if that happens while this factory is still running, so return first.
    await Task.Yield();
    try
    {
        logger.Information("Starting shutdown process...");
        await Task.WhenAll(bots.Select(bot => bot.Shutdown()));
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
