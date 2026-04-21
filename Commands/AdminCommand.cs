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

    internal AdminCommand(IBot originatingBot, CancellationToken cancellationToken = default)
        : base(originatingBot, cancellationToken) { }

    public override Task<bool> PrepareResponse()
    {
        if (string.IsNullOrWhiteSpace(MessageContext))
        {
            Message = "Usage: !admin shutdown | !admin <name> [\"message\" | <Xm> | <#channel> | on | off | now]";
            return Task.FromResult(true);
        }

        var afterCommand = MessageContext.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (afterCommand.Length < 2)
        {
            Message = "Usage: !admin shutdown | !admin <name> [\"message\" | <Xm> | <#channel> | on | off | now]";
            return Task.FromResult(true);
        }

        var subCommand = afterCommand[1].Trim();

        if (subCommand.Equals("shutdown", StringComparison.OrdinalIgnoreCase))
        {
            _logger.Warning("Shutdown requested via admin command.");
            Message = "Shutting down...";
            _ = OriginatingBot.RequestShutdown();
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
        var quotedMatch = Regex.Match(arg, @"^""(.+)""$");
        if (quotedMatch.Success)
        {
            scheduledMessage.Message = quotedMatch.Groups[1].Value;
            Message = $"'{scheduledMessage.Name}' message updated.";
            return Task.FromResult(true);
        }

        // Frequency: e.g. "40m" or "1h30m"
        var freqMatch = Regex.Match(arg, @"^(?:(\d+)h)?(\d+)m$");
        if (freqMatch.Success)
        {
            int hours = freqMatch.Groups[1].Success ? int.Parse(freqMatch.Groups[1].Value) : 0;
            int minutes = int.Parse(freqMatch.Groups[2].Value);
            scheduledMessage.FrequencyMinutes = hours * 60 + minutes;
            Message = $"'{scheduledMessage.Name}' frequency set to {scheduledMessage.FrequencyMinutes} minutes.";
            return Task.FromResult(true);
        }

        // Channel mention: <#123456789>
        var channelMatch = Regex.Match(arg, @"^<#(\d+)>$");
        if (channelMatch.Success && ulong.TryParse(channelMatch.Groups[1].Value, out var channelId))
        {
            if (!scheduledMessage.ChannelIds.Contains(channelId))
            {
                scheduledMessage.ChannelIds.Add(channelId);
                _logger.Information(
                    "Added channel {ChannelId} to '{Name}'.", channelId, scheduledMessage.Name
                );
            }
            Message = $"Added <#{channelId}> to '{scheduledMessage.Name}'.";
            return Task.FromResult(true);
        }

        Message = $"Unrecognized argument: '{arg}'.";
        return Task.FromResult(true);
    }
}
