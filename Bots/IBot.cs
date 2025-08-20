using MultiBot.Commands;

namespace MultiBot.Bots;

internal interface IBot
{
    string Name { get; }
    List<IBotCommand> Commands { get; }
    void OnCommand(string message);
    void Shutdown();
}
