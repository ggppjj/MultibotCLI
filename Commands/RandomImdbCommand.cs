using System.Drawing;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using CsvHelper;
using CsvHelper.Configuration;
using LibMultibot.Helper_Classes;
using LibMultibot.Interfaces;
using LibMultibot.Platforms;
using Serilog;

namespace MultibotCLI.Commands;

public struct InternalImdbData()
{
    public required string tconst;
    public required string titleType;
    public required string primaryTitle;
    public required string originalTitle;
    public required bool isAdult;
    public int? startYear;
    public int? endYear;
    public int? runtimeMinutes;
    public required string[] genres;
}

public struct ImdbData()
{
    public required string tconst;
}

public class ImdbDataMap : ClassMap<InternalImdbData>
{
    public ImdbDataMap()
    {
        Map(m => m.tconst).Convert(row => row.Row.GetField("tconst") ?? string.Empty);
        Map(m => m.titleType).Convert(row => row.Row.GetField("titleType") ?? string.Empty);
        Map(m => m.primaryTitle).Convert(row => row.Row.GetField("primaryTitle") ?? string.Empty);
        Map(m => m.originalTitle).Convert(row => row.Row.GetField("originalTitle") ?? string.Empty);
        Map(m => m.isAdult).Convert(row => row.Row.GetField("isAdult") == "1");

        Map(m => m.startYear)
            .Convert(row =>
            {
                var val = row.Row.GetField("startYear");
                if (string.IsNullOrEmpty(val) || val == "\\N")
                    return null;
                if (int.TryParse(val, out var year))
                    return year;
                return null;
            });

        Map(m => m.endYear)
            .Convert(row =>
            {
                var val = row.Row.GetField("endYear");
                if (string.IsNullOrEmpty(val) || val == "\\N")
                    return null;
                if (int.TryParse(val, out var year))
                    return year;
                return null;
            });

        Map(m => m.runtimeMinutes)
            .Convert(row =>
            {
                var val = row.Row.GetField("runtimeMinutes");
                if (string.IsNullOrEmpty(val) || val == "\\N")
                    return null;
                if (int.TryParse(val, out var mins))
                    return mins;
                return null;
            });

        Map(m => m.genres)
            .Convert(row =>
            {
                var val = row.Row.GetField("genres");
                if (string.IsNullOrEmpty(val) || val == "\\N")
                    return [];
                return val.Split(',', StringSplitOptions.RemoveEmptyEntries);
            });
    }
}

public class ImdbConfig
{
    public string ImdbDataUrl { get; set; } =
        "URL to title.basics.tsv.gz here, ensure your use of this data is in compliance with the terms of the IMDB license: https://help.imdb.com/article/imdb/general-information/can-i-use-imdb-data-in-my-software/G5JTRESSHJBBHTGX";
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ImdbConfig))]
internal partial class ImdbConfigJsonContext : JsonSerializerContext { }

internal class ImdbCommandConfig(string botName, string commandName, ILogger logger)
    : CommandConfigBase<ImdbConfig>(botName, commandName, logger)
{
    protected override JsonSerializerContext JsonContext => ImdbConfigJsonContext.Default;
    protected override JsonTypeInfo<ImdbConfig> JsonTypeInfo =>
        ImdbConfigJsonContext.Default.ImdbConfig;

    protected override ImdbConfig CreateDefaultConfig() => new();
}

internal class RandomImdbCommand : IBotCommand
{
    public IBot OriginatingBot { get; }
    public string Name { get; } = "random-imdb";
    public bool IsActive { get; set; } = true;
    public string Description { get; } = "Get a random movie or TV series from IMDB";
    private readonly ImdbCommandConfig _config;
    public List<BotPlatforms> CommandPlatforms { get; } = [BotPlatforms.Discord];
    public BotCommandTypes CommandType { get; } = BotCommandTypes.SlashCommand;
    private readonly ILogger _logger;
    public IBotResponse Response { get; }
    public CancellationToken CancellationToken { get; set; }

    internal List<ImdbData> ImdbDataList { get; set; } = [];

    internal RandomImdbCommand(IBot originatingBot, CancellationToken cancellationToken = default)
    {
        OriginatingBot = originatingBot;
        CancellationToken = cancellationToken;
        _logger = LogController.BotLogging.ForBotComponent<RandomImdbCommand>(OriginatingBot);
        _config = new ImdbCommandConfig(originatingBot.Name, Name, _logger);
        Response = new DiscordResponse(this);
    }

    internal class DiscordResponse(IBotCommand command) : IBotResponse
    {
        public BotPlatforms ResponsePlatform { get; } = BotPlatforms.Discord;
        public IBotCommand OriginatingCommand { get; } = command;
        public string? Message { get; set; } = "";
        public string? EmbedFilePath { get; set; } = null;
        public string? EmbedFileName { get; set; } = null;
        public Color? EmbedColor { get; } = null;
        public string? EmbedTitle { get; } = null;
        public string? EmbedDescription { get; } = null;
        public CancellationToken CancellationToken { get; set; }

        private readonly Random random = new();

        public Task<bool> PrepareResponse()
        {
            if (!OriginatingCommand.IsActive)
                return Task.FromResult(false);

            var imdb = (OriginatingCommand as RandomImdbCommand)!.ImdbDataList;

            if (imdb.Count == 0)
                return Task.FromResult(false);

            var index = random.Next(imdb.Count);
            var entry = imdb[index];

            Message = $"https://www.imdb.com/title/{entry.tconst}/";

            return Task.FromResult(true);
        }
    }

    public async Task<bool> Init()
    {
        var csvLocation = Path.Combine(AppContext.BaseDirectory, "Resources", "IMDB");
        var csvFileName = "title.basics.tsv.gz";
        var csvFullPath = Path.Combine(csvLocation, csvFileName);
        var url = _config.Config.ImdbDataUrl;

        if (!File.Exists(csvFullPath))
        {
            try
            {
                using HttpClient client = new();
                var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, url));

                if (!response.IsSuccessStatusCode)
                {
                    _logger.Error(response.StatusCode.ToString());
                    return false;
                }

                _logger.Information("Downloading csv...");

                if (!Directory.Exists(csvLocation))
                    Directory.CreateDirectory(csvLocation);

                using (response = await client.GetAsync(url, CancellationToken))
                {
                    response.EnsureSuccessStatusCode();
                    await using var downloadFileStream = new FileStream(
                        csvFullPath,
                        FileMode.Create
                    );
                    await response.Content.CopyToAsync(downloadFileStream, CancellationToken);
                }
            }
            catch
            {
                _logger.Error("Unable to reach CSV URL!");
                return false;
            }
        }

        await using var fileStream = new FileStream(
            csvFullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true
        );

        await using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
        using var decompressedStream = new MemoryStream();
        var buffer = new byte[81920];
        int bytesRead;
        _logger.Information("Loading IMDB data...");

        while ((bytesRead = await gzipStream.ReadAsync(buffer, CancellationToken)) > 0)
        {
            CancellationToken.ThrowIfCancellationRequested();
            decompressedStream.Write(buffer, 0, bytesRead);
        }

        decompressedStream.Position = 0;
        using var reader = new StreamReader(decompressedStream);

        var imdbCsvConfig = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            Delimiter = "\t",
            BadDataFound = null,
            MissingFieldFound = null,
        };

        using var csv = new CsvReader(reader, imdbCsvConfig);
        csv.Context.RegisterClassMap<ImdbDataMap>();
        var tempImdbDataList = new List<InternalImdbData>();

        await foreach (var record in csv.GetRecordsAsync<InternalImdbData>(CancellationToken))
        {
            if (
                !record.isAdult
                && (record.titleType == "movie" || record.titleType == "tvSeries")
                && record.startYear >= 1910
            )
            {
                tempImdbDataList.Add(record);
            }
        }

        ImdbDataList = [.. tempImdbDataList.Select(x => new ImdbData { tconst = x.tconst })];
        _logger.Information("IMDB data loaded!");
        return true;
    }
}
