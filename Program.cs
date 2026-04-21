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
var shutdownCompleted = false;

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

logger.Information("Starting...");

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    logger.Information("Received shutdown signal...");
    _ = Task.Run(Shutdown);
};
AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    logger.Information("Application process exiting...");
    _ = Task.Run(Shutdown);
};

try
{
    bots.Add(new TCHJRBot(shutdownCts));
    var initResults = await Task.WhenAll(bots.Select(b => b.Init()));
    if (initResults.All(r => !r))
    {
        logger.Fatal("All bots failed to initialize.");
        shutdownCts.Cancel();
    }
    else
        logger.Information("Started.");
    await Task.Delay(Timeout.Infinite, shutdownCts.Token);
}
catch (OperationCanceledException) { }
catch (Exception)
{
    logger.Fatal("Fatal error occurred during application execution.");
}
finally
{
    if (!shutdownCompleted)
        await Shutdown();
}

async Task Shutdown()
{
    if (!shutdownCompleted)
    {
        logger.Information("Starting shutdown process...");
        var tasks = new List<Task>();
        foreach (var bot in bots)
            tasks.Add(bot.Shutdown());
        await Task.WhenAll(tasks);
        shutdownCompleted = true;
        shutdownCts.Cancel();
        logger.Information("Application shutdown completed.");
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
