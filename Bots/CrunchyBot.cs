using LibMultibot;
using LibMultibot.Interfaces;
using LibMultibot.Platforms;
using LibMultibot.Users;
using MultibotCLI.Commands;
using MultibotCLI.Commands.Crunchy;

namespace MultibotCLI.Bots;

internal class CrunchyBot : BotBase
{
    public const string Version = "Public 10.0";

    public override string Name { get; } = "Crunchybot";

    private readonly CrunchyConfigStore _config;

    internal CrunchyBot(CancellationTokenSource shutdownSource) : base(shutdownSource)
    {
        _config = new CrunchyConfigStore(Name, _logger);

        // Read once: unlike the listeners, the admin command does not follow config edits.
        List<User> admins =
        [
            .. _config
                .Config.AdminUserIds.Append(_config.Config.OwnerUserId)
                .Where(id => id != 0)
                .Distinct()
                .Select(id => new User(id)),
        ];
        Commands.Add(new AdminCommand(this, CancellationToken) { AdminUsers = admins });
        Commands.Add(new SuperAdminCommand(this, CancellationToken));

        MessageListeners.Add(new CrunchyChatListener(this, _config));
        MessageListeners.Add(new CrunchyDirectMessageListener(this, _config));
    }

    protected override IEnumerable<IBotPlatform> CreatePlatforms()
    {
        yield return new DiscordPlatform(this);
    }

    protected override async Task OnStarted()
    {
        var channelId = _config.Config.StartupChannelId;
        if (channelId == 0)
            return;
        await SendMessage(
            $"Crunchybot {Version} loaded! Updates can be found here: {_config.Config.StatusUrl}",
            channelId
        );
    }
}
