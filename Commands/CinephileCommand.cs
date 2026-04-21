using System.Collections.Concurrent;
using System.Drawing;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using LibMultibot;
using LibMultibot.Helper_Classes;
using LibMultibot.Interfaces;
using LibMultibot.Platforms;
using Serilog;

namespace MultibotCLI.Commands;

public record struct MovieData()
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

internal class CinephileCommand : CommandBase
{
    public override string Name { get; } = "Cinephile";
    public override string Description { get; } =
        "Roll the dice and come up craps! See if you can get the photo you were hoping for, or set the tone!";
    public override List<BotPlatforms> CommandPlatforms { get; } = [BotPlatforms.Discord];
    public override BotCommandTypes CommandType { get; } =
        BotCommandTypes.SlashCommand | BotCommandTypes.TextCommand;
    public override Color? EmbedColor { get; } = Color.FromArgb(255, 255, 204, 0);

    internal int _lastSentIndex = -1;
    internal readonly ConcurrentDictionary<ulong, ConcurrentQueue<int>> _preloadMeQueues = new();
    internal readonly ConcurrentQueue<int> _preloadNextQueue = new();

    private readonly string _imagesDirectory = Path.Combine(
        AppContext.BaseDirectory,
        "Resources",
        "Images"
    );
    private readonly CinephileCommandConfig _config;

    internal CinephileCommand(IBot originatingBot, CancellationToken cancellationToken = default)
        : base(originatingBot, cancellationToken)
    {
        _config = new CinephileCommandConfig(originatingBot.Name, Name, _logger);
    }

    public override Task<bool> PrepareResponse()
    {
        if (!IsActive)
            return Task.FromResult(false);

        var movies = _config.Config.Movies;
        if (movies.Count == 0)
            return Task.FromResult(false);

        Message = null;
        EmbedTitle = null;
        EmbedDescription = null;
        EmbedFileName = null;
        EmbedFilePath = null;

        string? subCommand = null;
        string? subArg = null;

        if (!string.IsNullOrEmpty(MessageContext))
        {
            var parts = MessageContext.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
                subCommand = parts[1].ToLowerInvariant();
            if (parts.Length >= 3)
                subArg = parts[2];
        }

        if (subCommand != null && subCommand.StartsWith("admin"))
        {
            var adminArg = subCommand["admin".Length..];

            if (adminArg == "last")
            {
                if (_lastSentIndex < 0 || _lastSentIndex >= movies.Count)
                {
                    Message = "No movie has been sent yet.";
                    return Task.FromResult(true);
                }
                SetMovieEmbed(_lastSentIndex, movies, includeIndex: true);
                return Task.FromResult(true);
            }

            if (
                int.TryParse(adminArg, out int entryNumber)
                && entryNumber >= 1
                && entryNumber <= movies.Count
            )
            {
                SetMovieEmbed(entryNumber - 1, movies);
                return Task.FromResult(true);
            }

            Message = $"Invalid admin index. Use 1 to {movies.Count}.";
            return Task.FromResult(true);
        }
        else if (subCommand == "preloadme")
        {
            if (!MessageAuthorId.HasValue)
            {
                Message = "Cannot identify user for preloadme.";
                return Task.FromResult(true);
            }
            if (!int.TryParse(subArg, out int idx) || idx < 1 || idx > movies.Count)
            {
                Message = $"Invalid index. Use 1 to {movies.Count}.";
                return Task.FromResult(true);
            }
            var queue = _preloadMeQueues.GetOrAdd(
                MessageAuthorId.Value,
                _ => new ConcurrentQueue<int>()
            );
            queue.Enqueue(idx - 1);
            Message = $"Queued entry #{idx} for your next !cinephile.";
            return Task.FromResult(true);
        }
        else if (subCommand == "preloadnext")
        {
            if (!int.TryParse(subArg, out int idx) || idx < 1 || idx > movies.Count)
            {
                Message = $"Invalid index. Use 1 to {movies.Count}.";
                return Task.FromResult(true);
            }
            _preloadNextQueue.Enqueue(idx - 1);
            Message = $"Queued entry #{idx} for the next !cinephile.";
            return Task.FromResult(true);
        }
        else if (subCommand == "preloadclear")
        {
            _preloadMeQueues.Clear();
            while (_preloadNextQueue.TryDequeue(out _)) { }
            Message = "All preload queues cleared.";
            return Task.FromResult(true);
        }
        else
        {
            int idx;
            if (
                MessageAuthorId.HasValue
                && _preloadMeQueues.TryGetValue(MessageAuthorId.Value, out var userQueue)
                && userQueue.TryDequeue(out int preloadedMe)
                && preloadedMe >= 0
                && preloadedMe < movies.Count
            )
            {
                idx = preloadedMe;
            }
            else if (
                _preloadNextQueue.TryDequeue(out int preloadedNext)
                && preloadedNext >= 0
                && preloadedNext < movies.Count
            )
            {
                idx = preloadedNext;
            }
            else
            {
                idx = Random.Shared.Next(movies.Count);
            }
            SetMovieEmbed(idx, movies);
            return Task.FromResult(true);
        }
    }

    private void SetMovieEmbed(int index, List<MovieData> movies, bool includeIndex = false)
    {
        _lastSentIndex = index;
        var movie = movies[index];
        var oneBased = index + 1;
        EmbedDescription = includeIndex ? $"{movie.Description} {oneBased}" : movie.Description;
        EmbedTitle = movie.Title;
        EmbedFileName = movie.ImageFileName;
        EmbedFilePath = Path.Combine(_imagesDirectory, "cinephile", movie.ImageFileName);
    }
}
