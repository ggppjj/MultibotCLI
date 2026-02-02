using LibMultibot.Helper_Classes;
using LibMultibot.Interfaces;
using LibMultibot.Platforms;
using MultibotCLI.Commands;
using Serilog;

namespace MultibotCLI.Bots;

internal class TCHJRBot : IBot
{
    public string Name { get; } = "TCHJR";
    public List<IBotCommand> Commands { get; } = [];

    private readonly List<IBotPlatform> _platforms = [];
    private readonly ILogger _logger;

    public void OnCommand(string message) => Console.WriteLine("Event received: " + message);

    internal TCHJRBot()
    {
        _logger = LogController.SetupLogging(typeof(TCHJRBot));
        _logger.Information("Starting...");
        Commands.Add(new CinephileCommand(this));
        Commands.Add(new GnomeoCommand(this));
        Commands.Add(new RandomImdbCommand(this));
    }

    public async Task<bool> Init()
    {
        var initTasks = Commands.Select(c => c.Init()).ToList();
        var results = await Task.WhenAll(initTasks);

        for (int i = 0; i < results.Length; i++)
        {
            if (!results[i])
                _logger.Warning($"Command '{Commands[i].Name}' failed to initialize.");
        }

        try
        {
            _platforms.Add(new DiscordPlatform(this));
            _logger.Information("Started.");
            return true;
        }
        catch (InvalidDataException e)
        {
            _logger.Fatal(e.Message);
            throw;
        }
    }

    public async Task Shutdown()
    {
        _logger.Information("Shutting down...");
        var tasks = new List<Task>();
        foreach (var platform in _platforms)
            tasks.Add(platform.Shutdown());
        await Task.WhenAll(tasks);
        _logger.Information("Shutdown complete.");
    }
}
