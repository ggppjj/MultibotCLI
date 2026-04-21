using LibMultibot;
using LibMultibot.Interfaces;
using LibMultibot.Platforms;
using LibMultibot.Users;
using MultibotCLI.Commands;
using MultibotCLI.ScheduledMessages;

namespace MultibotCLI.Bots;

internal class TCHJRBot : BotBase
{
    public override string Name { get; } = "TCHJR";

    internal TCHJRBot(CancellationTokenSource shutdownSource) : base(shutdownSource)
    {
        List<User> admins =
        [
            new(142829604330143744),
            new(95599652551786496),
            new(183872699532050432),
            new(429354910384128000),
        ];
        Commands.Add(new CinephileCommand(this, CancellationToken));
        Commands.Add(new GnomeoCommand(this, CancellationToken));
        Commands.Add(new RandomImdbCommand(this, CancellationToken));
        var adminCommand = new AdminCommand(this, CancellationToken) { AdminUsers = admins };
        Commands.Add(adminCommand);
        var oscarFever = new OscarFever(adminCommand, this);
        ScheduledMessages!.Add(oscarFever);
        adminCommand.ManagedScheduledMessages.Add(oscarFever);
    }

    protected override IEnumerable<IBotPlatform> CreatePlatforms()
    {
        yield return new DiscordPlatform(this);
    }
}
