using MultiBot.Bots;
using MultiBot.Commands;

namespace MultiBot.Platforms;

enum BotPlatforms
{
    Discord,
}

internal interface IBotPlatform
{
    string Name { get; }
    IBot Bot { get; }
    List<IBotCommand> Commands { get; }
    void Shutdown();
}
