using System.Diagnostics;
using MultiBot.Interfaces;
using MultiBot.Platforms;

namespace MultiBot.Commands;

internal class CinephileCommand : IBotCommand
{
    public IBot OriginatingBot { get; }
    public string Name { get; } = "Cinephile";
    public string Description { get; } =
        "Roll the dice and come up craps! See if you can get the photo you were hoping for, or set the tone!";
    public List<BotPlatforms> CommandPlatforms { get; } = [BotPlatforms.Discord];
    public BotCommandTypes CommandType { get; } = BotCommandTypes.SlashCommand;
    private readonly string _fileName1 = "image1.png";
    private readonly string _fileName2 = "image2.png";
    private readonly string _fileName3 = "image3.png";

    private readonly string _imagePath1 = Path.Combine("Resources", "Images", "image1.png");
    private readonly string _imagePath2 = Path.Combine("Resources", "Images", "image2.png");
    private readonly string _imagePath3 = Path.Combine("Resources", "Images", "image3.png");

    public readonly List<MovieDataStruct> MovieData = [];

    public struct MovieDataStruct(
        string title,
        string description,
        string filePath,
        string fileName
    )
    {
        public string Title { get; set; } = title;
        public string Description { get; set; } = description;
        public string FilePath { get; set; } = filePath;
        public string FileName { get; set; } = fileName;
    }

    internal CinephileCommand(IBot originatingBot)
    {
        MovieData.Add(
            (new("Movie 1", "A dark film of adventure and friendship.", _imagePath1, _fileName1))
        );
        MovieData.Add(
            (new("Movie 2", "An epic tale of adventure and heroism.", _imagePath2, _fileName2))
        );
        MovieData.Add(
            (
                new(
                    "Movie 3",
                    "A classic adventure film that never gets old.",
                    _imagePath3,
                    _fileName3
                )
            )
        );
        Response = new DiscordResponse(this);
        OriginatingBot = originatingBot;
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

        private readonly CinephileCommand originatingCinephileCommand = (
            command as CinephileCommand
        )!;

        public Task<bool> PrepareResponse()
        {
            var rng = new Random();
            var randomMovie = originatingCinephileCommand.MovieData[
                rng.Next(originatingCinephileCommand.MovieData.Count)
            ];
            Message = randomMovie.Description;
            EmbedFilePath = randomMovie.FilePath;
            EmbedFileName = randomMovie.FileName;
            EmbedTitle = randomMovie.Title;
            EmbedDescription = randomMovie.Description;
            return Task.FromResult(true);
        }
    }
}
