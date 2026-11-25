using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using LibMultibot.Helper_Classes;
using LibMultibot.Interfaces;
using Serilog;

namespace MultibotCLI.ScheduledMessages;

public class OscarFeverConfig
{
    public bool IsEnabled { get; set; } = false;
    public string Message { get; set; } = "I've got the Oscar Fever!";
    public int FrequencyMinutes { get; set; } = 45;
    public List<ulong> ChannelIds { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(OscarFeverConfig))]
internal partial class OscarFeverConfigJsonContext : JsonSerializerContext { }

internal class OscarFeverCommandConfig(string botName, string commandName, ILogger logger)
    : CommandConfigBase<OscarFeverConfig>(botName, commandName, logger)
{
    protected override JsonSerializerContext JsonContext => OscarFeverConfigJsonContext.Default;
    protected override JsonTypeInfo<OscarFeverConfig> JsonTypeInfo =>
        OscarFeverConfigJsonContext.Default.OscarFeverConfig;

    protected override OscarFeverConfig CreateDefaultConfig() => new();

    /// <summary>Raised after the config file is (re)loaded, including runtime edits.</summary>
    public event Action? Reloaded;

    protected override void OnConfigLoaded() => Reloaded?.Invoke();
}

public class OscarFever : IBotScheduledMessage
{
    // System.Timers.Timer rejects intervals over int.MaxValue milliseconds (about 24.8 days).
    // Checked here too because the frequency can be hand-edited in the config file.
    private const int MinFrequencyMinutes = 1;
    private const int MaxFrequencyMinutes = 35791;

    public string Name => "Oscar Fever";
    public IBot OriginatingBot { get; }
    public string Description => "Sends a message every so often.";

    public string Message
    {
        get => _config.Config.Message;
        set
        {
            _config.Config.Message = value;
            SaveSettings();
            _logger.Information("Message updated: {Message}", value);
        }
    }

    public int FrequencyMinutes
    {
        get => Math.Clamp(_config.Config.FrequencyMinutes, MinFrequencyMinutes, MaxFrequencyMinutes);
        set
        {
            _config.Config.FrequencyMinutes = value;
            SaveSettings();
            _logger.Information("Frequency updated: {FrequencyMinutes} minutes", value);
            if (_timer.Enabled)
                _timer.Interval = TimeSpan.FromMinutes(FrequencyMinutes).TotalMilliseconds;
        }
    }

    public bool IsEnabled
    {
        get => _config.Config.IsEnabled;
        set
        {
            _config.Config.IsEnabled = value;
            SaveSettings();
        }
    }

    private readonly System.Timers.Timer _timer = new();
    private readonly ILogger _logger;
    private readonly OscarFeverCommandConfig _config;

    // Guards the channel list, which admin commands change while the timer thread reads it.
    private readonly object _channelLock = new();
    public IBotCommand ManagementCommand { get; }
    public event Action<object> OnReply = _ => { };

    public OscarFever(IBotCommand managementCommand, IBot originatingBot)
    {
        ManagementCommand = managementCommand;
        OriginatingBot = originatingBot;
        _logger = LogController.BotLogging.ForBotComponent<OscarFever>(OriginatingBot);
        _config = new OscarFeverCommandConfig(originatingBot.Name, "OscarFever", _logger);
        _timer.AutoReset = true;
        _timer.Elapsed += (_, _) => TriggerNow();
        _config.Reloaded += ApplySettings;
        ApplySettings();
    }

    public void Start()
    {
        IsEnabled = true;
        ApplySettings();
        _logger.Information("Enabled. Firing every {FrequencyMinutes} minutes.", FrequencyMinutes);
    }

    public void Stop()
    {
        IsEnabled = false;
        ApplySettings();
        _logger.Information("Disabled.");
    }

    public bool AddChannel(ulong channelId)
    {
        lock (_channelLock)
        {
            if (_config.Config.ChannelIds.Contains(channelId))
                return false;
            _config.Config.ChannelIds.Add(channelId);
            SaveSettings();
            return true;
        }
    }

    public bool RemoveChannel(ulong channelId)
    {
        lock (_channelLock)
        {
            if (!_config.Config.ChannelIds.Remove(channelId))
                return false;
            SaveSettings();
            return true;
        }
    }

    public void TriggerNow()
    {
        // The timer outlives a bot that was shut down on its own.
        if (!OriginatingBot.IsActive)
            return;

        ulong[] channelIds;
        lock (_channelLock)
            channelIds = [.. _config.Config.ChannelIds];

        _logger.Information("Firing message to {ChannelCount} channel(s).", channelIds.Length);
        foreach (var channelId in channelIds)
            _ = SendAsync(channelId);
    }

    // Brings the timer in line with the settings, at startup and after a config file edit.
    private void ApplySettings()
    {
        _timer.Stop();
        _timer.Interval = TimeSpan.FromMinutes(FrequencyMinutes).TotalMilliseconds;
        if (IsEnabled)
            _timer.Start();
    }

    private void SaveSettings()
    {
        try
        {
            _config.Save();
        }
        catch (Exception ex)
        {
            // The change still applies until the next restart.
            _logger.Error(ex, "Failed to save the {Name} settings.", Name);
        }
    }

    private async Task SendAsync(ulong channelId)
    {
        try
        {
            await OriginatingBot.SendMessage(Message, channelId, true);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to send message to {ChannelId}.", channelId);
        }
    }
}
