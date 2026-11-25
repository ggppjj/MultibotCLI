using System.Text.RegularExpressions;
using LibMultibot;
using LibMultibot.Interfaces;
using LibMultibot.Platforms;

namespace MultibotCLI.Commands;

public class AdminCommand : CommandBase
{
    public override string Name { get; } = "Admin";
    public override string Description { get; } = "Admin Command";
    public override BotCommandTypes CommandType { get; } = BotCommandTypes.TextCommand;
    public override List<BotPlatforms> CommandPlatforms { get; } = [BotPlatforms.Discord];
    public override bool IsAdminCommand { get; } = true;
    public List<IBotScheduledMessage> ManagedScheduledMessages { get; } = [];

    private const string Usage =
        "Usage: !admin shutdown | !admin <name> [\"message\" | <interval> | <#channel> | remove <#channel> | on | off | now]";

    // System.Timers.Timer rejects intervals over int.MaxValue milliseconds (about 24.8 days).
    private const int MaxFrequencyMinutes = 35791;

    // Gives the reply to the shutdown command time to send.
    private static readonly TimeSpan ShutdownDelay = TimeSpan.FromSeconds(2);

    internal AdminCommand(IBot originatingBot, CancellationToken cancellationToken = default)
        : base(originatingBot, cancellationToken) { }

    public override Task<bool> PrepareResponse()
    {
        if (string.IsNullOrWhiteSpace(MessageContext))
        {
            Message = Usage;
            return Task.FromResult(true);
        }

        var afterCommand = MessageContext.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (afterCommand.Length < 2)
        {
            Message = Usage;
            return Task.FromResult(true);
        }

        var subCommand = afterCommand[1].Trim();

        if (subCommand.Equals("shutdown", StringComparison.OrdinalIgnoreCase))
        {
            _logger.Warning("Shutdown of this bot requested via admin command.");
            Message = $"Shutting down {OriginatingBot.Name}...";
            _ = Task.Run(async () =>
            {
                await Task.Delay(ShutdownDelay);
                await OriginatingBot.RequestBotShutdown();
            });
            return Task.FromResult(true);
        }

        var subParts = subCommand.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var slug = subParts[0].ToLowerInvariant();

        var scheduledMessage = ManagedScheduledMessages
            .FirstOrDefault(m => m.Name.ToLowerInvariant().Replace(" ", "") == slug);

        if (scheduledMessage == null)
        {
            Message = $"No managed scheduled message found matching '{subParts[0]}'.";
            return Task.FromResult(true);
        }

        if (subParts.Length < 2)
        {
            _logger.Information("Manually triggered '{Name}'.", scheduledMessage.Name);
            scheduledMessage.TriggerNow();
            Message = $"Triggered '{scheduledMessage.Name}'.";
            return Task.FromResult(true);
        }

        var arg = subParts[1].Trim();

        if (arg.Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            _logger.Information("Enabled '{Name}'.", scheduledMessage.Name);
            scheduledMessage.Start();
            Message = $"'{scheduledMessage.Name}' enabled.";
            return Task.FromResult(true);
        }

        if (arg.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            _logger.Information("Disabled '{Name}'.", scheduledMessage.Name);
            scheduledMessage.Stop();
            Message = $"'{scheduledMessage.Name}' disabled.";
            return Task.FromResult(true);
        }

        if (arg.Equals("now", StringComparison.OrdinalIgnoreCase))
        {
            _logger.Information("Manually triggered '{Name}'.", scheduledMessage.Name);
            scheduledMessage.TriggerNow();
            Message = $"Triggered '{scheduledMessage.Name}'.";
            return Task.FromResult(true);
        }

        // Quoted string: new message text e.g. "I've got the Oscar Fever!"
        var quotedMatch = Regex.Match(arg, @"^""(.+)""$", RegexOptions.Singleline);
        if (quotedMatch.Success)
        {
            scheduledMessage.Message = quotedMatch.Groups[1].Value;
            Message = $"'{scheduledMessage.Name}' message updated.";
            return Task.FromResult(true);
        }

        // Channel mention: <#123456789>
        var channelMatch = Regex.Match(arg, @"^<#(\d+)>$");
        if (channelMatch.Success && ulong.TryParse(channelMatch.Groups[1].Value, out var channelId))
        {
            if (scheduledMessage.AddChannel(channelId))
            {
                _logger.Information(
                    "Added channel {ChannelId} to '{Name}'.", channelId, scheduledMessage.Name
                );
            }
            Message = $"Added <#{channelId}> to '{scheduledMessage.Name}'.";
            return Task.FromResult(true);
        }

        // Channel removal: remove <#123456789>
        var removeMatch = Regex.Match(arg, @"^remove\s+<#(\d+)>$", RegexOptions.IgnoreCase);
        if (removeMatch.Success && ulong.TryParse(removeMatch.Groups[1].Value, out var removeChannelId))
        {
            if (scheduledMessage.RemoveChannel(removeChannelId))
            {
                _logger.Information(
                    "Removed channel {ChannelId} from '{Name}'.", removeChannelId, scheduledMessage.Name
                );
                Message = $"Removed <#{removeChannelId}> from '{scheduledMessage.Name}'.";
            }
            else
            {
                Message = $"<#{removeChannelId}> is not on '{scheduledMessage.Name}'.";
            }
            return Task.FromResult(true);
        }

        // Frequency: e.g. "40m", "2h" or "1h30m"
        if (DurationText.TryParse(arg, out var frequency))
        {
            var minutes = frequency.TotalMinutes;
            if (minutes < 1 || minutes > MaxFrequencyMinutes || minutes % 1 != 0)
            {
                Message = $"Frequency must be a whole number of minutes from 1 to {MaxFrequencyMinutes}.";
                return Task.FromResult(true);
            }
            scheduledMessage.FrequencyMinutes = (int)minutes;
            Message = $"'{scheduledMessage.Name}' frequency set to {scheduledMessage.FrequencyMinutes} minutes.";
            return Task.FromResult(true);
        }

        Message = $"Unrecognized argument: '{arg}'.";
        return Task.FromResult(true);
    }
}
