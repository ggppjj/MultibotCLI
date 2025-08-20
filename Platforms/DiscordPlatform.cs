using Discord;
using Discord.Net;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using MultiBot.Bots;
using MultiBot.Commands;
using MultiBot.Logging;
using Newtonsoft.Json;
using Serilog;

namespace MultiBot.Platforms;

public delegate void CommandEventHandler(object? sender, EventArgs? e);

internal class DiscordPlatform : IBotPlatform
{
    public string Name { get; } = "Discord";
    public IBot Bot { get; }
    public List<IBotCommand> Commands { get; } = [];

    private readonly IConfiguration _tokenConfig = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("DiscordTokens.json", optional: true, reloadOnChange: true)
        .Build();
    private readonly DiscordSocketClient _discordClient;
    private readonly ILogger _logger;
    private readonly DiscordSocketConfig _discordClientConfig =
        new() { LogLevel = LogSeverity.Info };
    private readonly string? _token;

    public event CommandEventHandler? OnCommand;

    internal DiscordPlatform(LogController logController, IBot bot)
    {
        Bot = bot;
        _logger = logController.SetupLogging(typeof(DiscordPlatform));
        _logger.Information($"Starting for {Bot.Name}...");
        _token = _tokenConfig[Bot.Name];
        if (string.IsNullOrWhiteSpace(_token))
        {
            _logger.Fatal("Missing bot token in DiscordTokens.json file");
            throw new InvalidDataException("Missing bot token!");
        }
        _discordClient = new(_discordClientConfig);
        _discordClient.Log += LogDiscordMessageToSerilog;
        _discordClient.SlashCommandExecuted += SlashCommandHandler;
        _discordClient.Ready += OnClientReady;
        LoadCommands();
        StartAsync().GetAwaiter().GetResult();
        _logger.Information($"Started for {Bot.Name}.");
    }

    public async Task StartAsync()
    {
        if (string.IsNullOrWhiteSpace(_token))
        {
            _logger.Fatal("Missing bot token in token.json file");
            throw new InvalidDataException("Missing bot token!");
        }
        await _discordClient.LoginAsync(TokenType.Bot, _token);
        await _discordClient.StartAsync();
    }

    async Task SlashCommandHandler(SocketSlashCommand command)
    {
        try
        {
            var matchingCommand = Commands.FirstOrDefault(c => c.Name == command.CommandName);
            var response = matchingCommand?.Response(BotPlatforms.Discord);
            if (matchingCommand is not null && response is not null and Embed embed)
            {
                await command.RespondAsync(embed: embed);
            }
            else
            {
                await command.RespondAsync("Unknown command.", ephemeral: true);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"Error processing slash command '{command.CommandName}'");
            await command.RespondAsync(
                "An error occurred while processing your command.",
                ephemeral: true
            );
        }
    }

    async Task OnClientReady()
    {
        List<ApplicationCommandProperties> applicationCommandProperties = [];
        foreach (var command in Commands)
        {
            applicationCommandProperties.Add(
                new SlashCommandBuilder()
                    .WithName(command.Name)
                    .WithDescription(command.Description)
                    .Build()
            );
        }
        try
        {
            _ = await _discordClient.BulkOverwriteGlobalApplicationCommandsAsync(
                [.. applicationCommandProperties]
            );
        }
        catch (HttpException exception)
        {
            Console.WriteLine(JsonConvert.SerializeObject(exception.Errors, Formatting.Indented));
        }
    }

    private void LoadCommands()
    {
        foreach (var command in Commands)
        {
            if (command.CommandPlatforms.Contains(BotPlatforms.Discord))
                Commands.Add(command);
        }
    }

    public void Shutdown()
    {
        _logger.Information($"Shutting down for {Bot.Name}...");
        _logger.Information($"Shutdown for {Bot.Name} complete.");
    }

    private static Task LogDiscordMessageToSerilog(LogMessage message)
    {
        switch (message.Severity)
        {
            case LogSeverity.Critical:
                Log.Fatal(message.Exception, message.Message);
                break;
            case LogSeverity.Error:
                Log.Error(message.Exception, message.Message);
                break;
            case LogSeverity.Warning:
                Log.Warning(message.Message);
                break;
            case LogSeverity.Info:
                Log.Information(message.Message);
                break;
            case LogSeverity.Verbose:
            case LogSeverity.Debug:
                Log.Debug(message.Message);
                break;
        }
        return Task.CompletedTask;
    }

    protected virtual void RaiseCommandEvent(EventArgs e) => OnCommand?.Invoke(this, e);
}
