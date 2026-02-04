using System.Drawing;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using LibMultibot.Helper_Classes;
using LibMultibot.Interfaces;
using LibMultibot.Platforms;
using Serilog;

namespace MultibotCLI.Commands;

public struct GnomeoData()
{
    public string Title { get; set; } = string.Empty;
    public string Quote { get; set; } = string.Empty;
    public string ImageFileName { get; set; } = string.Empty;
}

public class GnomeoConfig
{
    public List<GnomeoData> Gnomeo { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(GnomeoConfig))]
internal partial class GnomeoConfigJsonContext : JsonSerializerContext { }

internal class GnomeoCommandConfig(string botName, string commandName, ILogger logger)
    : CommandConfigBase<GnomeoConfig>(botName, commandName, logger)
{
    protected override JsonSerializerContext JsonContext => GnomeoConfigJsonContext.Default;
    protected override JsonTypeInfo<GnomeoConfig> JsonTypeInfo =>
        GnomeoConfigJsonContext.Default.GnomeoConfig;

    protected override GnomeoConfig CreateDefaultConfig()
    {
        return new GnomeoConfig
        {
            Gnomeo =
            [
                new()
                {
                    Quote = "Nice name. It really goes with your...eyes.",
                    Title = "Gnomeo",
                    ImageFileName = "gnomeo1.png",
                },
                new()
                {
                    Quote = "Well, I grabbed it first, but if you want it, come get it.",
                    Title = "Gnomeo",
                    ImageFileName = "gnomeo2.png",
                },
                new()
                {
                    Quote = "Who's your gnomie?",
                    Title = "Gnomeo",
                    ImageFileName = "gnomeo3.png",
                },
                new()
                {
                    Quote = "Well, this isn't my greenhouse.",
                    Title = "Gnomeo",
                    ImageFileName = "gnomeo4.png",
                },
                new()
                {
                    Quote = "Nice greenhouse, eh?",
                    Title = "Gnomeo",
                    ImageFileName = "gnomeo5.png",
                },
            ],
        };
    }
}

internal class GnomeoCommand : IBotCommand
{
    public IBot OriginatingBot { get; }
    public string Name { get; } = "Gnomeo";
    public string Description { get; } = "Gnomeo.";
    public List<BotPlatforms> CommandPlatforms { get; } = [BotPlatforms.Discord];
    public bool IsActive { get; set; } = true;
    public BotCommandTypes CommandType { get; } =
        BotCommandTypes.SlashCommand | BotCommandTypes.TextCommand;
    private readonly string _imagesDirectory = Path.Combine(
        AppContext.BaseDirectory,
        "Resources",
        "Images",
        "Gnomeo"
    );

    private readonly GnomeoCommandConfig _config;
    private readonly ILogger _logger;
    public IBotResponse Response { get; }

    internal GnomeoCommand(IBot originatingBot)
    {
        OriginatingBot = originatingBot;
        _logger = Log.Logger.ForContext<GnomeoCommand>();
        _config = new GnomeoCommandConfig(originatingBot.Name, Name, _logger);
        Response = new DiscordResponse(this);
    }

    internal class DiscordResponse(IBotCommand command) : IBotResponse
    {
        public BotPlatforms ResponsePlatform { get; } = BotPlatforms.Discord;
        public string? Message { get; } = null;
        public string? EmbedFilePath { get; set; } = null;
        public string? EmbedFileName { get; set; } = null;
        public string? EmbedTitle { get; set; } = null;
        public string? EmbedDescription { get; set; } = "Gnome.";
        public Color? EmbedColor { get; } = Color.FromArgb(255, 22, 44, 115);
        public IBotCommand OriginatingCommand { get; } = command;

        public Task<bool> PrepareResponse()
        {
            if (!OriginatingCommand.IsActive)
                return Task.FromResult(false);

            var gnomeo = (OriginatingCommand as GnomeoCommand)!._config.Config.Gnomeo;

            if (gnomeo.Count == 0)
                return Task.FromResult(false);

            var randomGnomeo = gnomeo[Random.Shared.Next(gnomeo.Count)];
            EmbedTitle = randomGnomeo.Title;
            EmbedDescription = randomGnomeo.Quote;
            EmbedFileName = randomGnomeo.ImageFileName;
            EmbedFilePath = Path.Combine(
                (OriginatingCommand as GnomeoCommand)!._imagesDirectory,
                randomGnomeo.ImageFileName
            );

            return Task.FromResult(true);
        }
    }

    public Task<bool> Init() => Task.FromResult(true);
}
