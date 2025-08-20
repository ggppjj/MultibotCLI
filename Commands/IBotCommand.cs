using MultiBot.Platforms;

namespace MultiBot.Commands;

internal interface IBotCommand
{
    string Name { get; }
    string Description { get; }
    Func<BotPlatforms, object> Response { get; }
    List<BotPlatforms> CommandPlatforms { get; }
}
