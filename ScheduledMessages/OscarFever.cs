using LibMultibot.Helper_Classes;
using LibMultibot.Interfaces;
using Serilog;

namespace MultibotCLI.ScheduledMessages;

public class OscarFever : IBotScheduledMessage
{
    public string Name => "Oscar Fever";
    public IBot OriginatingBot { get; }
    public string Description => "Sends a message every so often.";

    public string Message
    {
        get => _message;
        set
        {
            _message = value;
            _logger.Information("Message updated: {Message}", value);
        }
    }
    private string _message = "I've got the Oscar Fever!";

    public int FrequencyMinutes
    {
        get => _frequencyMinutes;
        set
        {
            _frequencyMinutes = value;
            _logger.Information("Frequency updated: {FrequencyMinutes} minutes", value);
            if (_timer.Enabled)
                _timer.Interval = TimeSpan.FromMinutes(value).TotalMilliseconds;
        }
    }
    private int _frequencyMinutes = 45;

    public bool IsEnabled { get; set; } = false;

    private readonly System.Timers.Timer _timer = new();
    private readonly ILogger _logger;
    public IBotCommand ManagementCommand { get; }
    public event Action<object> OnReply = _ => { };
    public List<ulong> ChannelIds { get; } = [];

    public OscarFever(IBotCommand managementCommand, IBot originatingBot)
    {
        ManagementCommand = managementCommand;
        OriginatingBot = originatingBot;
        _logger = LogController.BotLogging.ForBotComponent<OscarFever>(OriginatingBot);
        _timer.AutoReset = true;
        _timer.Elapsed += (_, _) => TriggerNow();
    }

    public void Start()
    {
        _timer.Interval = TimeSpan.FromMinutes(FrequencyMinutes).TotalMilliseconds;
        _timer.Start();
        IsEnabled = true;
        _logger.Information("Enabled. Firing every {FrequencyMinutes} minutes.", FrequencyMinutes);
    }

    public void Stop()
    {
        _timer.Stop();
        IsEnabled = false;
        _logger.Information("Disabled.");
    }

    public void TriggerNow()
    {
        _logger.Information("Firing message to {ChannelCount} channel(s).", ChannelIds.Count);
        foreach (var channelId in ChannelIds)
            _ = OriginatingBot.SendMessage(Message, channelId, true);
    }
}
