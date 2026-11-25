using LibMultibot;
using LibMultibot.Interfaces;
using LibMultibot.Platforms;
using LibMultibot.Users;

namespace MultibotCLI.Commands;

/// <summary>
/// Commands for the owner alone. "!superadmin shutdown" stops the whole process and every
/// bot in it, where "!admin shutdown" stops only the bot it was sent to.
/// </summary>
public class SuperAdminCommand : CommandBase
{
    private const ulong OwnerUserId = 142829604330143744; // ggppjj

    public override string Name { get; } = "SuperAdmin";
    public override string Description { get; } = "Super Admin Command";
    public override BotCommandTypes CommandType { get; } = BotCommandTypes.TextCommand;
    public override List<BotPlatforms> CommandPlatforms { get; } = [BotPlatforms.Discord];
    public override bool IsAdminCommand { get; } = true;

    private const string Usage = "Usage: !superadmin shutdown";

    // Gives the reply to the shutdown command time to send.
    private static readonly TimeSpan ShutdownDelay = TimeSpan.FromSeconds(2);

    internal SuperAdminCommand(IBot originatingBot, CancellationToken cancellationToken = default)
        : base(originatingBot, cancellationToken)
    {
        AdminUsers = [new User(OwnerUserId)];
    }

    public override Task<bool> PrepareResponse()
    {
        // The platform checks AdminUsers, but that list can be replaced from outside.
        if (MessageAuthorId != OwnerUserId)
        {
            _logger.Warning("Super admin command refused for user {UserId}.", MessageAuthorId);
            Message = "You can't use this command.";
            return Task.FromResult(true);
        }

        var parts = (MessageContext ?? string.Empty).Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries
        );
        if (parts.Length != 2 || !parts[1].Equals("shutdown", StringComparison.OrdinalIgnoreCase))
        {
            Message = Usage;
            return Task.FromResult(true);
        }

        _logger.Warning("Shutdown of the whole process requested via super admin command.");
        Message = "Shutting down every bot...";
        _ = Task.Run(async () =>
        {
            await Task.Delay(ShutdownDelay);
            await OriginatingBot.RequestProcessShutdown();
        });
        return Task.FromResult(true);
    }
}
