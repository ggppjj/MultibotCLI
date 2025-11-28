using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using MultiBot.Helper_Classes;
using MultiBot.Interfaces;
using MultiBot.Platforms;
using Serilog;

namespace MultiBot.Commands;

public class MovieData
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

internal class CinephileCommandConfig : CommandConfigBase<CinephileConfig>
{
    protected override JsonSerializerContext JsonContext => CinephileConfigJsonContext.Default;
    protected override JsonTypeInfo<CinephileConfig> JsonTypeInfo =>
        CinephileConfigJsonContext.Default.CinephileConfig;

    public CinephileCommandConfig(string botName, string commandName, ILogger logger)
        : base(botName, commandName, logger) { }

    protected override CinephileConfig CreateDefaultConfig()
    {
        return new CinephileConfig
        {
            Movies =
            [
                new MovieData
                {
                    Title = "Movie 1",
                    Description = "A dark film of adventure and friendship.",
                    ImageFileName = "image1.png",
                },
                new MovieData
                {
                    Title = "Movie 2",
                    Description = "An epic tale of adventure and heroism.",
                    ImageFileName = "image2.png",
                },
                new MovieData
                {
                    Title = "Movie 3",
                    Description = "A classic adventure film that never gets old.",
                    ImageFileName = "image3.png",
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
    public BotCommandTypes CommandType { get; } =
        BotCommandTypes.SlashCommand | BotCommandTypes.TextCommand;

    private readonly string _imagesDirectory = Path.Combine(
        AppContext.BaseDirectory,
        "Resources",
        "Images"
    );
    private readonly CinephileCommandConfig _config;
    private readonly ILogger _logger;

    internal CinephileCommand(IBot originatingBot)
    {
        _logger = Log.Logger.ForContext<CinephileCommand>();
        OriginatingBot = originatingBot;

        _config = new CinephileCommandConfig(originatingBot.Name, Name, _logger);

        Response = new DiscordResponse(this);
    }

    public IBotResponse Response { get; }

    internal class DiscordResponse(IBotCommand command) : IBotResponse
    {
        public BotPlatforms ResponsePlatform { get; } = BotPlatforms.Discord;
        public IBotCommand OriginatingCommand { get; } = command;
        public string Message { get; set; } = "Here's your cinephile image!";
        public string? EmbedFilePath { get; set; } = null;
        public string? EmbedFileName { get; set; } = null;
        public string? EmbedTitle { get; set; } = null;
        public string? EmbedDescription { get; set; } = null;

        private readonly CinephileCommand _originatingCinephileCommand = (
            command as CinephileCommand
        )!;

        public Task<bool> PrepareResponse()
        {
            var movies = _originatingCinephileCommand._config.Config.Movies;

            if (movies.Count == 0)
            {
                return Task.FromResult(false);
            }

            var rng = new Random();
            var randomMovie = movies[rng.Next(movies.Count)];

            Message = randomMovie.Description;
            EmbedTitle = randomMovie.Title;
            EmbedDescription = randomMovie.Description;
            EmbedFileName = randomMovie.ImageFileName;
            EmbedFilePath = Path.Combine(
                _originatingCinephileCommand._imagesDirectory,
                randomMovie.ImageFileName
            );

            return Task.FromResult(true);
        }
    }
}
