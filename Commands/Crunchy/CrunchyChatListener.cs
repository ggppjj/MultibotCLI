using System.Text.RegularExpressions;
using LibMultibot.Helper_Classes;
using LibMultibot.Interfaces;
using Serilog;

namespace MultibotCLI.Commands.Crunchy;

/// <summary>Text shared by Crunchybot's server and direct message listeners.</summary>
internal static class CrunchyText
{
    public const string UnknownCommand =
        "I'm sorry, but CrunchyBot does not understand this command. For a list of commands I will accept, use the \"help\" command.";

    private const string AdminHelp =
        "Admin DM commands: shodan [channel ID] <message>, xerxes <user ID> <message>.";

    private const string ShodanHelp = "Admin DM commands: shodan [channel ID] <message>.";

    public static string Help(CrunchyConfigStore config, ulong userId) =>
        "Say \"Crunchybot, <command>\" to use crunchybot.\n"
        + "Current commands: Post, Help, About, How are you?, !g, Umad?, Ujelly?, What's new?.\n"
        + "DM commands: about, help."
        + (
            config.IsAdmin(userId) ? "\n" + AdminHelp
            : config.CanShodan(userId) ? "\n" + ShodanHelp
            : ""
        );

    public static string About(string ownerName) =>
        $"Made by {ownerName}! Got a way to improve me? Let {ownerName} know!\n"
        + "Thanks to Ventero, for help with RegExp's and everything else! Thanks to Kong for Letting me make this! Special thanks to Spikinator, for abusing the system, and Tuluvme for being awesome, and Eklei for being patient, and Sirtoinou for being French!";

    // Names go into the bot's own messages, where "@everyone" in a display name would ping.
    public static string Safe(string name) => name.Replace("@", "@​");

    public static string Truncate(string text) => text.Length <= 2000 ? text : text[..2000];
}

/// <summary>
/// Crunchybot in server channels: "Crunchybot, &lt;command&gt;" (or a leading mention), and
/// the replies it volunteers to messages that were not addressed to it.
/// </summary>
internal partial class CrunchyChatListener(IBot bot, CrunchyConfigStore config)
    : IBotMessageListener
{
    public string Name => "CrunchyChat";
    public bool IsActive { get; set; } = true;

    private readonly ILogger _logger = LogController.BotLogging.ForBotComponent(
        typeof(CrunchyChatListener),
        bot
    );

    private static readonly TimeSpan TheGameDelay = TimeSpan.FromMilliseconds(1500);
    private readonly Lock _theGameLock = new();
    private readonly Dictionary<ulong, DateTime> _theGameLastSent = [];

    private enum Command
    {
        Post,
        Umad,
        Ujelly,
        Help,
        About,
        HowAreYou,
        LoseGame,
        WhatsNew,
    }

    [GeneratedRegex(@"^crunchy\s?bot,?\s+(.+)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex PrefixPattern();

    [GeneratedRegex(@"the\sgame|!g", RegexOptions.IgnoreCase)]
    private static partial Regex TheGamePattern();

    [GeneratedRegex(@"^how\sare\syou", RegexOptions.IgnoreCase)]
    private static partial Regex HowAreYouPattern();

    [GeneratedRegex(@"what'?s\s*new", RegexOptions.IgnoreCase)]
    private static partial Regex WhatsNewPattern();

    public async Task OnMessage(BotMessage message, IBotPlatform platform)
    {
        if (message.IsDirectMessage)
            return;

        var commandText = ExtractCommand(message);
        if (commandText != null)
        {
            await HandleCommand(commandText, message, platform);
            return;
        }

        // At most one of these answers a message, in this order.
        var cfg = config.Config;
        var isOwner = config.IsOwner(message.AuthorId);
        if (!isOwner && config.Matches(config.ImpostorName, message.AuthorDisplayName))
        {
            await platform.SendMessage(
                $"That was {CrunchyText.Safe(message.AuthorUsername)}!",
                message.ChannelId
            );
        }
        else if (cfg.TheGameEnabled && TheGamePattern().IsMatch(message.Content))
        {
            LoseTheGame(message.ChannelId, platform);
        }
        else if (!isOwner && config.Matches(config.NameMisspelling, message.Content))
        {
            await platform.SendMessage(
                $"His name is {cfg.OwnerName}. Just Sayin'.",
                message.ChannelId
            );
        }
    }

    private static string? ExtractCommand(BotMessage message)
    {
        var prefixed = PrefixPattern().Match(message.Content);
        if (prefixed.Success)
            return prefixed.Groups[1].Value;

        if (message.BotUserId == 0)
            return null;
        foreach (var mention in new[] { $"<@{message.BotUserId}>", $"<@!{message.BotUserId}>" })
        {
            if (!message.Content.StartsWith(mention, StringComparison.Ordinal))
                continue;
            var rest = message.Content[mention.Length..].TrimStart(',').Trim();
            return rest.Length > 0 ? rest : null;
        }
        return null;
    }

    private static List<Command> MatchCommands(string text)
    {
        bool Has(string word) => text.Contains(word, StringComparison.OrdinalIgnoreCase);

        List<Command> matches = [];
        if (Has("post"))
            matches.Add(Command.Post);
        if (Has("umad"))
            matches.Add(Command.Umad);
        if (Has("ujelly"))
            matches.Add(Command.Ujelly);
        if (Has("help"))
            matches.Add(Command.Help);
        if (Has("about"))
            matches.Add(Command.About);
        if (HowAreYouPattern().IsMatch(text))
            matches.Add(Command.HowAreYou);
        if (Has("!g"))
            matches.Add(Command.LoseGame);
        if (WhatsNewPattern().IsMatch(text))
            matches.Add(Command.WhatsNew);
        return matches;
    }

    private async Task HandleCommand(string text, BotMessage message, IBotPlatform platform)
    {
        var cfg = config.Config;
        var user = CrunchyText.Safe(message.AuthorDisplayName);
        var channel = message.ChannelId;
        var matches = MatchCommands(text);

        if (matches.Count == 0)
        {
            await platform.SendDirectMessage(message.AuthorId, CrunchyText.UnknownCommand);
            return;
        }
        if (matches.Count == Enum.GetValues<Command>().Length)
        {
            await platform.SendMessage("Wow. You really need a new hobby.", channel);
            return;
        }
        if (matches.Count > 1)
        {
            await platform.SendMessage($"I'm sorry, {user}, but I can't do that.", channel);
            return;
        }

        switch (matches[0])
        {
            case Command.Post:
                await platform.SendMessage(
                    $"Current status can be found here: {cfg.StatusUrl}",
                    channel
                );
                break;
            case Command.WhatsNew:
                await platform.SendMessage($"Updates can be found here: {cfg.StatusUrl}", channel);
                break;
            // Swapped on purpose: each answers with the other's question.
            case Command.Umad:
                await platform.SendMessage($"Naw. Ujelly, {user}?", channel);
                break;
            case Command.Ujelly:
                await platform.SendMessage($"Nope. Umad, {user}?", channel);
                break;
            case Command.HowAreYou:
                await platform.SendMessage(
                    $"I feel fine, {user}. Why are you asking a robot, though?",
                    channel
                );
                break;
            case Command.LoseGame:
                await platform.SendMessage("!g", channel);
                await platform.SendDirectMessage(
                    message.AuthorId,
                    config.IsOwner(message.AuthorId)
                        ? "Good one!"
                        : $"That wasn't very nice, {user}."
                );
                break;
            case Command.Help:
                await SendByDirectMessage(
                    CrunchyText.Help(config, message.AuthorId),
                    message,
                    platform
                );
                break;
            case Command.About:
                await SendByDirectMessage(CrunchyText.About(cfg.OwnerName), message, platform);
                break;
        }
    }

    private static async Task SendByDirectMessage(
        string text,
        BotMessage message,
        IBotPlatform platform
    )
    {
        var user = CrunchyText.Safe(message.AuthorDisplayName);
        var sent = await platform.SendDirectMessage(message.AuthorId, text);
        await platform.SendMessage(
            sent ? $"PM sent to {user}!" : $"{user}, I couldn't PM you. Do you allow DMs?",
            message.ChannelId
        );
    }

    private void LoseTheGame(ulong channelId, IBotPlatform platform)
    {
        var cooldown = TimeSpan.FromMinutes(Math.Max(0, config.Config.TheGameCooldownMinutes));
        var now = DateTime.UtcNow;
        lock (_theGameLock)
        {
            if (
                _theGameLastSent.TryGetValue(channelId, out var lastSent)
                && now - lastSent < cooldown
            )
                return;
            _theGameLastSent[channelId] = now;
        }

        // Not awaited: the pause before answering must not hold up handling of the message.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TheGameDelay, bot.CancellationToken);
                await platform.SendMessage("You just lost The Game.", channelId);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.Error(ex, "Couldn't post The Game in channel {ChannelId}.", channelId);
            }
        });
    }
}
