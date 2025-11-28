using MultiBot.Platforms;

namespace MultiBot.Interfaces;

public enum BotCommandTypes
{
    SlashCommand,
}

public interface IBotCommand
{
    string Name { get; }
    string Description { get; }
    BotCommandTypes CommandType { get; }
    IBotResponse Response { get; }
    List<BotPlatforms> CommandPlatforms { get; }
    IBot OriginatingBot { get; }
}
