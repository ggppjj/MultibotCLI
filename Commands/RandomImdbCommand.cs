using System.Globalization;
using System.IO.Compression;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using CsvHelper;
using LibMultibot;
using LibMultibot.Helper_Classes;
using LibMultibot.Interfaces;
using LibMultibot.Platforms;
using Serilog;

namespace MultibotCLI.Commands;

public struct ImdbData()
{
    public required string tconst;
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
    private volatile bool _isInitialized = false;
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
                // Downloaded under a temp name, so an interrupted download can't be taken for
                // the finished file on the next start.
                var downloadPath = csvFullPath + ".tmp";
                try
                {
                    // No overall timeout: this is a large file, and the init watchdog already
                    // cancels the download if it stalls.
                    using HttpClient client = new() { Timeout = Timeout.InfiniteTimeSpan };
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

                    using var response = await client.GetAsync(
                        url,
                        HttpCompletionOption.ResponseHeadersRead,
                        CancellationToken
                    );
                    response.EnsureSuccessStatusCode();
                    await using (var downloadFileStream = new FileStream(downloadPath, FileMode.Create))
                    await using (var body = await response.Content.ReadAsStreamAsync(CancellationToken))
                    {
                        var downloadBuffer = new byte[81920];
                        int downloaded;
                        while ((downloaded = await body.ReadAsync(downloadBuffer, CancellationToken)) > 0)
                        {
                            // Keeps the init watchdog from cancelling a download in progress.
                            InitProgress?.Report("downloading");
                            await downloadFileStream.WriteAsync(
                                downloadBuffer.AsMemory(0, downloaded),
                                CancellationToken
                            );
                        }
                    }
                    File.Move(downloadPath, csvFullPath, overwrite: true);
                }
                catch (OperationCanceledException)
                {
                    if (File.Exists(downloadPath))
                        File.Delete(downloadPath);
                    throw;
                }
                catch (Exception ex)
                {
                    if (File.Exists(downloadPath))
                        File.Delete(downloadPath);
                    _logger.Error(ex, "Unable to download the CSV!");
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

            // IMDB's TSV has no quoting: quote characters in titles are literal text.
            var imdbCsvOptions = new CsvReaderOptions
            {
                HasHeader = true,
                Separator = '\t',
                Mode = CsvMode.NoEscape,
            };

            await using var csv = CsvReader.Create(decompressedStream, imdbCsvOptions);
            await csv.ReadHeaderAsync(CancellationToken);
            var tconstColumn = csv.GetFieldIndex("tconst");
            var titleTypeColumn = csv.GetFieldIndex("titleType");
            var isAdultColumn = csv.GetFieldIndex("isAdult");
            var startYearColumn = csv.GetFieldIndex("startYear");
            var tempImdbDataList = new List<ImdbData>();

            while (await csv.ReadAsync(CancellationToken))
            {
                InitProgress?.Report("parsing");
                if (
                    !csv.GetSpan(isAdultColumn).SequenceEqual("1"u8)
                    && (
                        csv.GetSpan(titleTypeColumn).SequenceEqual("movie"u8)
                        || csv.GetSpan(titleTypeColumn).SequenceEqual("tvSeries"u8)
                    )
                    // A missing year is "\N", which does not parse and so is excluded.
                    && int.TryParse(csv.GetSpan(startYearColumn), CultureInfo.InvariantCulture, out var startYear)
                    && startYear >= 1910
                )
                {
                    tempImdbDataList.Add(new ImdbData { tconst = csv.GetString(tconstColumn) });
                }
            }

            ImdbDataList = tempImdbDataList;
            _logger.Information("IMDB data loaded!");
            _isInitialized = true;
            return true;
        }
        catch (OperationCanceledException)
        {
            _logger.Warning("IMDB load cancelled by shutdown or the init watchdog.");
            return false;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "IMDB initialization failed.");
            return false;
        }
    }
}
