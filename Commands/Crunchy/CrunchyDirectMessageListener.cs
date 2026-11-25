using System.Text.RegularExpressions;
using LibMultibot.Helper_Classes;
using LibMultibot.Interfaces;
using Serilog;

namespace MultibotCLI.Commands.Crunchy;

/// <summary>
/// Crunchybot's direct message commands: shodan (speak in a channel) and xerxes (relay a
/// DM) for admins, about and help for everyone.
/// </summary>
internal partial class CrunchyDirectMessageListener(IBot bot, CrunchyConfigStore config)
    : IBotMessageListener
{
    public string Name => "CrunchyDirectMessages";
    public bool IsActive { get; set; } = true;

    private readonly ILogger _logger = LogController.BotLogging.ForBotComponent(
        typeof(CrunchyDirectMessageListener),
        bot
    );

    // The channel is a <#mention> or a bare ID, since DMs can't autocomplete server channels.
    // A bare ID must be snowflake length, so a message that starts with a number still works.
    [GeneratedRegex(
        @"^shodan\s+(?:(?:<#(\d+)>|(\d{17,20}))\s+)?(.+)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline
    )]
    private static partial Regex ShodanPattern();

    [GeneratedRegex(
        @"^xerxes\s+(?:<@!?(\d+)>|(\d+))\s+(.+)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline
    )]
    private static partial Regex XerxesPattern();

    public async Task OnMessage(BotMessage message, IBotPlatform platform)
    {
        if (!message.IsDirectMessage)
            return;

        var text = message.Content.Trim();
        if (text.Length == 0)
            return;

        var cfg = config.Config;
        Task Reply(string reply) => platform.SendMessage(reply, message.ChannelId);

        var shodan = ShodanPattern().Match(text);
        var xerxes = XerxesPattern().Match(text);
        if (shodan.Success)
            await Shodan(shodan, message, platform);
        else if (xerxes.Success)
            await Xerxes(xerxes, message, platform);
        else if (text.StartsWith("about", StringComparison.OrdinalIgnoreCase))
            await Reply(CrunchyText.About(cfg.OwnerName));
        else if (text.StartsWith("help", StringComparison.OrdinalIgnoreCase))
            await Reply(CrunchyText.Help(config, message.AuthorId));
        else
        {
            await Reply(CrunchyText.UnknownCommand);
            if (
                cfg.ForwardDirectMessagesToOwner
                && cfg.OwnerUserId != 0
                && !config.IsOwner(message.AuthorId)
            )
                await platform.SendDirectMessage(
                    cfg.OwnerUserId,
                    CrunchyText.Truncate($"{message.AuthorUsername}: {message.Content}")
                );
        }
    }

    private async Task Shodan(Match match, BotMessage message, IBotPlatform platform)
    {
        var cfg = config.Config;
        Task Reply(string reply) => platform.SendMessage(reply, message.ChannelId);

        if (!config.CanShodan(message.AuthorId))
        {
            await Reply("LOLNO");
            return;
        }

        var channelId =
            match.Groups[1].Success ? ulong.Parse(match.Groups[1].Value)
            : match.Groups[2].Success ? ulong.Parse(match.Groups[2].Value)
            : cfg.ShodanChannelId;
        if (channelId == 0)
        {
            await Reply("No shodan channel is set. Use: shodan <channel ID> <message>");
            return;
        }

        try
        {
            await platform.SendMessage(match.Groups[3].Value, channelId);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Shodan couldn't post in channel {ChannelId}.", channelId);
            await Reply($"I couldn't post in <#{channelId}>.");
            return;
        }

        _logger.Information(
            "Shodan: {Username} ({UserId}) spoke in channel {ChannelId}.",
            message.AuthorUsername,
            message.AuthorId,
            channelId
        );
        if (config.IsOwner(message.AuthorId))
        {
            await Reply("Yes, Master");
            return;
        }
        await Reply($"Said it in <#{channelId}>.");
        if (cfg.OwnerUserId != 0)
            await platform.SendDirectMessage(
                cfg.OwnerUserId,
                $"That Was {message.AuthorUsername}."
            );
    }

    private async Task Xerxes(Match match, BotMessage message, IBotPlatform platform)
    {
        Task Reply(string reply) => platform.SendMessage(reply, message.ChannelId);

        if (!config.IsAdmin(message.AuthorId))
        {
            await Reply($"No. Bad {message.AuthorUsername}!");
            return;
        }

        var target = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
        if (!ulong.TryParse(target, out var targetId))
        {
            await Reply("That's not a user.");
            return;
        }

        var sent = await platform.SendDirectMessage(targetId, match.Groups[3].Value);
        _logger.Information(
            "Xerxes: {Username} ({UserId}) sent a PM to {TargetId}: {Result}.",
            message.AuthorUsername,
            message.AuthorId,
            targetId,
            sent ? "delivered" : "failed"
        );
        await Reply(
            sent
                ? $"PM sent to <@{targetId}> from {message.AuthorUsername}!"
                : $"I couldn't PM <@{targetId}>."
        );
    }
}
