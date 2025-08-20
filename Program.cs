using Microsoft.Extensions.Configuration;
using MultiBot.Bots;
using MultiBot.Logging;
using Serilog;

_ = args;

List<IBot> bots = [];

CancellationTokenSource? shutdownCts = new();
var shutdownCompleted = false;

var localApplicationConfig = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("config.json", optional: true, reloadOnChange: true)
    .Build();

var logController = new LogController();
var logger = logController.SetupLogging(typeof(Program), localApplicationConfig);

logger.Information("Starting...");
bots.Add(new TCHJR(logController));

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    logger.Information("Received shutdown signal...");
    Shutdown();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    logger.Information("Application process exiting...");
    Shutdown();
};

try
{
    logger.Information("Started.");
    Task.Delay(Timeout.Infinite, shutdownCts.Token).Wait();
}
catch (AggregateException ex) when (ex.InnerException is OperationCanceledException) { }
catch (Exception ex)
{
    logger.Fatal(ex, "Fatal error occurred during application execution.");
}
finally
{
    if (!shutdownCompleted)
        Shutdown();
}

void Shutdown()
{
    if (!shutdownCompleted)
    {
        logger.Information("Starting shutdown process...");
        foreach (var bot in bots)
            bot.Shutdown();
        shutdownCompleted = true;
        shutdownCts?.Cancel();
        logger.Information("Application shutdown completed.");
        Log.CloseAndFlush();
    }
}
