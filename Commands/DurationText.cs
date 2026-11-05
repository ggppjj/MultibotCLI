using TimeSpanParserUtil;

namespace MultibotCLI.Commands;

internal static class DurationText
{
    // Uses the parser's list form because its single-value form silently ignores anything
    // after the first value ("1m 2m" gives 1 minute) and prints exceptions to the console.
    public static bool TryParse(string text, out TimeSpan duration)
    {
        duration = default;
        try
        {
            if (!TimeSpanParser.TryParse(text, out TimeSpan[] durations) || durations.Length != 1)
                return false;
            duration = durations[0];
            return true;
        }
        catch (Exception ex) when (ex is OverflowException or FormatException or ArgumentException)
        {
            return false;
        }
    }
}
