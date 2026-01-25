using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using MultiBot.Bots;
using MultiBot.Helper_Classes;
using MultiBot.Interfaces;
using Serilog;

_ = args;

List<IBot> bots = [];

CancellationTokenSource? shutdownCts = new();
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
    _ = Task.Run(async () => await Shutdown());
};
AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    logger.Information("Application process exiting...");
    _ = Task.Run(async () => await Shutdown());
};

try
{
    bots.Add(new TCHJRBot());
    foreach (var bot in bots)
        await bot.Init();
    logger.Information("Started.");
    Task.Delay(Timeout.Infinite, shutdownCts.Token).Wait();
}
catch (AggregateException ex) when (ex.InnerException is OperationCanceledException) { }
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
        shutdownCts?.Cancel();
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
