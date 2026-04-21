using System.Drawing;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using LibMultibot;
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

internal class GnomeoCommand : CommandBase
{
    public override string Name { get; } = "Gnomeo";
    public override string Description { get; } = "Gnomeo.";
    public override List<BotPlatforms> CommandPlatforms { get; } = [BotPlatforms.Discord];
    public override BotCommandTypes CommandType { get; } =
        BotCommandTypes.SlashCommand | BotCommandTypes.TextCommand;
    public override Color? EmbedColor { get; } = Color.FromArgb(255, 22, 44, 115);

    private readonly string _imagesDirectory = Path.Combine(
        AppContext.BaseDirectory,
        "Resources",
        "Images",
        "Gnomeo"
    );
    private readonly GnomeoCommandConfig _config;

    internal GnomeoCommand(IBot originatingBot, CancellationToken cancellationToken = default)
        : base(originatingBot, cancellationToken)
    {
        _config = new GnomeoCommandConfig(originatingBot.Name, Name, _logger);
    }

    public override Task<bool> PrepareResponse()
    {
        if (!IsActive)
            return Task.FromResult(false);

        var gnomeo = _config.Config.Gnomeo;

        if (gnomeo.Count == 0)
            return Task.FromResult(false);

        var randomGnomeo = gnomeo[Random.Shared.Next(gnomeo.Count)];
        EmbedTitle = randomGnomeo.Title;
        EmbedDescription = randomGnomeo.Quote;
        EmbedFileName = randomGnomeo.ImageFileName;
        EmbedFilePath = Path.Combine(_imagesDirectory, randomGnomeo.ImageFileName);

        return Task.FromResult(true);
    }
}
