using System.Drawing;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using LibMultibot.Helper_Classes;
using LibMultibot.Interfaces;
using LibMultibot.Platforms;
using Serilog;

namespace MultibotCLI.Commands;

public struct MovieData()
{
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ImageFileName { get; set; } = string.Empty;
}

public class CinephileConfig
{
    public List<MovieData> Movies { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(CinephileConfig))]
internal partial class CinephileConfigJsonContext : JsonSerializerContext { }

internal class CinephileCommandConfig(string botName, string commandName, ILogger logger)
    : CommandConfigBase<CinephileConfig>(botName, commandName, logger)
{
    protected override JsonSerializerContext JsonContext => CinephileConfigJsonContext.Default;
    protected override JsonTypeInfo<CinephileConfig> JsonTypeInfo =>
        CinephileConfigJsonContext.Default.CinephileConfig;

    protected override CinephileConfig CreateDefaultConfig()
    {
        return new CinephileConfig
        {
            Movies =
            [
                new()
                {
                    Title = "Movie 1",
                    Description = "A dark film of adventure and friendship.",
                    ImageFileName = "cinephile1.png",
                },
                new()
                {
                    Title = "Movie 2",
                    Description = "An epic tale of adventure and heroism.",
                    ImageFileName = "cinephile2.png",
                },
                new()
                {
                    Title = "Movie 3",
                    Description = "A classic adventure film that never gets old.",
                    ImageFileName = "cinephile3.png",
                },
            ],
        };
    }
}

internal class CinephileCommand : IBotCommand
{
    public IBot OriginatingBot { get; }
    public string Name { get; } = "Cinephile";
    public string Description { get; } =
        "Roll the dice and come up craps! See if you can get the photo you were hoping for, or set the tone!";
    public List<BotPlatforms> CommandPlatforms { get; } = [BotPlatforms.Discord];
    public bool IsActive { get; set; } = true;
    public BotCommandTypes CommandType { get; } =
        BotCommandTypes.SlashCommand | BotCommandTypes.TextCommand;
    public IBotResponse Response { get; }

    private readonly string _imagesDirectory = Path.Combine(
        AppContext.BaseDirectory,
        "Resources",
        "Images"
    );
    private readonly CinephileCommandConfig _config;
    private readonly ILogger _logger;

    internal CinephileCommand(IBot originatingBot)
    {
        OriginatingBot = originatingBot;
        _logger = Log.Logger.ForContext<CinephileCommand>();
        _config = new CinephileCommandConfig(originatingBot.Name, Name, _logger);
        Response = new CinephileResponse(this);
    }

    internal class CinephileResponse(IBotCommand command) : IBotResponse
    {
        public BotPlatforms ResponsePlatform { get; } = BotPlatforms.Discord;
        public IBotCommand OriginatingCommand { get; } = command;
        public string? Message { get; } = null;
        public string? EmbedFilePath { get; set; } = null;
        public string? EmbedFileName { get; set; } = null;
        public Color? EmbedColor { get; } = Color.FromArgb(255, 247, 206, 70);
        public System.Drawing.Color test = new();
        public string? EmbedTitle { get; set; } = null;
        public string? EmbedDescription { get; set; } = "movie";

        private readonly CinephileCommand _originatingCinephileCommand = (
            command as CinephileCommand
        )!;

        public Task<bool> PrepareResponse()
        {
            if (!OriginatingCommand.IsActive)
                return Task.FromResult(false);

            var movies = _originatingCinephileCommand._config.Config.Movies;

            if (movies.Count == 0)
                return Task.FromResult(false);

            var randomMovie = movies[Random.Shared.Next(movies.Count)];
            test = System.Drawing.Color.FromArgb(0, 0, 0, 0);
            EmbedDescription = randomMovie.Description;
            EmbedTitle = randomMovie.Title;
            EmbedFileName = randomMovie.ImageFileName;
            EmbedFilePath = Path.Combine(
                _originatingCinephileCommand._imagesDirectory,
                "cinephile",
                randomMovie.ImageFileName
            );

            return Task.FromResult(true);
        }
    }

    public Task<bool> Init() => Task.FromResult(true);
}
