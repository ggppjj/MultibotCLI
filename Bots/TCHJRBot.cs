using LibMultibot.Helper_Classes;
using LibMultibot.Interfaces;
using LibMultibot.Platforms;
using LibMultibot.Users;
using MultibotCLI.Commands;
using MultibotCLI.ScheduledMessages;
using Serilog;

namespace MultibotCLI.Bots;

internal class TCHJRBot : IBot
{
    public string Name { get; } = "TCHJR";
    public List<IBotCommand> Commands { get; } = [];
    public List<IBotScheduledMessage>? ScheduledMessages { get; } = [];

    private readonly List<IBotPlatform> _platforms = [];
    private readonly ILogger _logger;
    public bool IsActive { get; set; } = true;
    public CancellationToken CancellationToken { get; set; }

    public void OnCommand(string message) => Console.WriteLine("Event received: " + message);

    private readonly CancellationTokenSource _shutdownSource;

    internal TCHJRBot(CancellationTokenSource shutdownSource)
    {
        List<User> admins =
        [
            new(142829604330143744),
            new(95599652551786496),
            new(183872699532050432),
            new(429354910384128000),
        ];
        _shutdownSource = shutdownSource;
        CancellationToken = shutdownSource.Token;
        _logger = LogController.SetupLogging(typeof(TCHJRBot));
        _logger.Information("Starting...");
        Commands.Add(new CinephileCommand(this, CancellationToken));
        Commands.Add(new GnomeoCommand(this, CancellationToken));
        Commands.Add(new RandomImdbCommand(this, CancellationToken));
        var adminCommand = new AdminCommand(this, CancellationToken) { AdminUsers = admins };
        Commands.Add(adminCommand);
        var oscarFever = new OscarFever(adminCommand, this);
        ScheduledMessages.Add(oscarFever);
        adminCommand.ManagedScheduledMessages.Add(oscarFever);
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

    public async Task SendMessage(string message, ulong channelId, bool trackedMessage = false)
    {
        foreach (IBotPlatform platform in _platforms)
            await platform.SendMessage(message, channelId, trackedMessage);
    }

    public Task RequestShutdown()
    {
        _shutdownSource.Cancel();
        return Task.CompletedTask;
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
