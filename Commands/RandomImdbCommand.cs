using System.Globalization;
using System.IO.Compression;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using CsvHelper;
using CsvHelper.Configuration;
using LibMultibot;
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

internal class RandomImdbCommand : CommandBase, IHeartbeatInit
{
    public override string Name { get; } = "random-imdb";
    public override string Description { get; } = "Get a random movie or TV series from IMDB";
    public override List<BotPlatforms> CommandPlatforms { get; } = [BotPlatforms.Discord];
    public override BotCommandTypes CommandType { get; } = BotCommandTypes.SlashCommand;
    private bool _isInitialized = false;
    public override bool IsInitialized => _isInitialized;
    public IProgress<string>? InitProgress { get; set; }

    internal List<ImdbData> ImdbDataList { get; set; } = [];
    private readonly ImdbCommandConfig _config;

    internal RandomImdbCommand(IBot originatingBot, CancellationToken cancellationToken = default)
        : base(originatingBot, cancellationToken)
    {
        _config = new ImdbCommandConfig(originatingBot.Name, Name, _logger);
    }

    public override Task<bool> PrepareResponse()
    {
        if (!IsActive)
            return Task.FromResult(false);

        if (!IsInitialized)
        {
            Message = "Still loading IMDB data, please try again shortly.";
            return Task.FromResult(true);
        }

        if (ImdbDataList.Count == 0)
            return Task.FromResult(false);

        var index = Random.Shared.Next(ImdbDataList.Count);
        Message = $"https://www.imdb.com/title/{ImdbDataList[index].tconst}/";

        return Task.FromResult(true);
    }

    public override async Task<bool> Init()
    {
        try
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
                    using (var headResponse = await client.SendAsync(
                        new HttpRequestMessage(HttpMethod.Head, url),
                        CancellationToken
                    ))
                    {
                        if (!headResponse.IsSuccessStatusCode)
                        {
                            _logger.Error(headResponse.StatusCode.ToString());
                            return false;
                        }
                    }

                    _logger.Information("Downloading csv...");

                    if (!Directory.Exists(csvLocation))
                        Directory.CreateDirectory(csvLocation);

                    using var response = await client.GetAsync(url, CancellationToken);
                    response.EnsureSuccessStatusCode();
                    await using var downloadFileStream = new FileStream(
                        csvFullPath,
                        FileMode.Create
                    );
                    await response.Content.CopyToAsync(downloadFileStream, CancellationToken);
                }
                catch (OperationCanceledException)
                {
                    if (File.Exists(csvFullPath))
                        File.Delete(csvFullPath);
                    throw;
                }
                catch
                {
                    if (File.Exists(csvFullPath))
                        File.Delete(csvFullPath);
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
                InitProgress?.Report("decompressing");
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
                InitProgress?.Report("parsing");
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
            _isInitialized = true;
            return true;
        }
        catch (OperationCanceledException)
        {
            _logger.Information("IMDB load cancelled due to shutdown.");
            return false;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "IMDB initialization failed.");
            return false;
        }
    }
}
