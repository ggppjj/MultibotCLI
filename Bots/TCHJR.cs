using MultiBot.Commands;
using MultiBot.Logging;
using MultiBot.Platforms;
using Serilog;

namespace MultiBot.Bots;

internal class TCHJR : IBot
{
    public string Name { get; } = "TCHJR";
    public List<IBotCommand> Commands { get; } = [];

    private readonly List<IBotPlatform> _platforms = [];
    private readonly ILogger _logger;

    public void OnCommand(string message) => Console.WriteLine("Event received: " + message);

    internal TCHJR(LogController logController)
    {
        _logger = logController.SetupLogging(typeof(TCHJR));
        _logger.Information("Starting...");
        Commands.Add(new Cinephile());
        _platforms.Add(new DiscordPlatform(logController, this));
        _logger.Information("Started.");
    }

    public void Shutdown()
    {
        _logger.Information("Shutting down...");
        foreach (var platform in _platforms)
            platform.Shutdown();
        _logger.Information("Shutdown complete.");
    }
}
