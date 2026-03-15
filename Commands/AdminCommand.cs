using System.Drawing;
using System.Text.RegularExpressions;
using LibMultibot.Helper_Classes;
using LibMultibot.Interfaces;
using LibMultibot.Platforms;
using LibMultibot.Users;
using Serilog;

namespace MultibotCLI.Commands;

public class AdminCommand : IBotCommand
{
    public string Name { get; } = "Admin";
    public string Description { get; } = "Admin Command";
    public BotCommandTypes CommandType { get; } = BotCommandTypes.TextCommand;
    public IBotResponse Response { get; }
    public List<BotPlatforms> CommandPlatforms { get; } = [BotPlatforms.Discord];
    public IBot OriginatingBot { get; }
    private readonly ILogger _logger;
    public bool IsActive { get; set; } = true;
    public CancellationToken CancellationToken { get; set; }
    public bool IsAdminCommand { get; } = true;
    public List<User>? AdminUsers { get; set; } = [];
    public List<ulong>? RestrictedToChannelIDs { get; set; } = [];
    public string? MessageContext { get; set; }
    public List<IBotScheduledMessage> ManagedScheduledMessages { get; } = [];

    public Task<bool> Init() => Task.FromResult(true);

    internal AdminCommand(IBot originatingBot, CancellationToken cancellationToken = default)
    {
        OriginatingBot = originatingBot;
        CancellationToken = cancellationToken;
        _logger = LogController.BotLogging.ForBotComponent<AdminCommand>(OriginatingBot);
        Response = new AdminResponse(this);
    }

    internal class AdminResponse(AdminCommand command) : IBotResponse
    {
        public BotPlatforms ResponsePlatform { get; } = BotPlatforms.Discord;
        public IBotCommand OriginatingCommand { get; } = command;
        public string? Message { get; set; }
        public string? EmbedFilePath { get; } = null;
        public string? EmbedFileName { get; } = null;
        public Color? EmbedColor { get; } = null;
        public string? EmbedTitle { get; } = null;
        public string? EmbedDescription { get; } = null;
        public CancellationToken CancellationToken { get; set; }
        private readonly AdminCommand _adminCommand = command;

        public Task<bool> PrepareResponse()
        {
            var context = _adminCommand.MessageContext;
            if (string.IsNullOrWhiteSpace(context))
            {
                Message = "Usage: !admin shutdown | !admin <name> [\"message\" | <Xm> | <#channel> | on | off | now]";
                return Task.FromResult(true);
            }

            // e.g. "!admin shutdown" or "!admin oscarfever on"
            var afterCommand = context.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (afterCommand.Length < 2)
            {
                Message = "Usage: !admin shutdown | !admin <name> [\"message\" | <Xm> | <#channel> | on | off | now]";
                return Task.FromResult(true);
            }

            var subCommand = afterCommand[1].Trim();

            if (subCommand.Equals("shutdown", StringComparison.OrdinalIgnoreCase))
            {
                _adminCommand._logger.Warning("Shutdown requested via admin command.");
                Message = "Shutting down...";
                _ = _adminCommand.OriginatingBot.RequestShutdown();
                return Task.FromResult(true);
            }

            var subParts = subCommand.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var slug = subParts[0].ToLowerInvariant().Replace(" ", "");

            var scheduledMessage = _adminCommand.ManagedScheduledMessages
                .FirstOrDefault(m => m.Name.ToLowerInvariant().Replace(" ", "") == slug);

            if (scheduledMessage == null)
            {
                Message = $"No managed scheduled message found matching '{subParts[0]}'.";
                return Task.FromResult(true);
            }

            if (subParts.Length < 2)
            {
                _adminCommand._logger.Information("Manually triggered '{Name}'.", scheduledMessage.Name);
                scheduledMessage.TriggerNow();
                Message = $"Triggered '{scheduledMessage.Name}'.";
                return Task.FromResult(true);
            }

            var arg = subParts[1].Trim();

            if (arg.Equals("on", StringComparison.OrdinalIgnoreCase))
            {
                _adminCommand._logger.Information("Enabled '{Name}'.", scheduledMessage.Name);
                scheduledMessage.Start();
                Message = $"'{scheduledMessage.Name}' enabled.";
                return Task.FromResult(true);
            }

            if (arg.Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                _adminCommand._logger.Information("Disabled '{Name}'.", scheduledMessage.Name);
                scheduledMessage.Stop();
                Message = $"'{scheduledMessage.Name}' disabled.";
                return Task.FromResult(true);
            }

            if (arg.Equals("now", StringComparison.OrdinalIgnoreCase))
            {
                _adminCommand._logger.Information("Manually triggered '{Name}'.", scheduledMessage.Name);
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
                    _adminCommand._logger.Information(
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
}
