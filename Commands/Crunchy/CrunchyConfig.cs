using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using LibMultibot.Helper_Classes;
using Serilog;

namespace MultibotCLI.Commands.Crunchy;

public class CrunchyConfig
{
    /// <summary>Gets forwarded DMs and the owner-only replies. 0 = nobody.</summary>
    public ulong OwnerUserId { get; set; }

    /// <summary>The owner's name as it should be spelled.</summary>
    public string OwnerName { get; set; } = "ggppjj";

    /// <summary>May use the shodan and xerxes DM commands, along with the owner.</summary>
    public List<ulong> AdminUserIds { get; set; } = [];

    /// <summary>May use the shodan DM command but not xerxes. Admins can already use it.</summary>
    public List<ulong> ShodanUserIds { get; set; } = [];

    /// <summary>Link given by the "post" and "what's new" commands.</summary>
    public string StatusUrl { get; set; } = "http://crunchybot.tumblr.com/";

    /// <summary>Where "shodan" speaks when the command names no channel. 0 = none.</summary>
    public ulong ShodanChannelId { get; set; }

    /// <summary>Where the loaded message is posted on startup. 0 = not posted.</summary>
    public ulong StartupChannelId { get; set; }

    public bool TheGameEnabled { get; set; } = true;

    /// <summary>Per channel.</summary>
    public int TheGameCooldownMinutes { get; set; } = 20;

    /// <summary>Regex for misspellings of the owner's name in a message. Empty = off.</summary>
    public string NameMisspellingPattern { get; set; } =
        "qqppjj|qqppii|ggppii|gqppii|gqppij|gqppji|qgppii|qgppij|qgppji";

    /// <summary>Regex for display names imitating the owner. Empty = off.</summary>
    public string ImpostorNamePattern { get; set; } = "qarde123|qqppjj|qgppjj|ggppji|ggppii|ggppij";

    /// <summary>Forward DMs the bot does not understand to the owner.</summary>
    public bool ForwardDirectMessagesToOwner { get; set; } = true;
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(CrunchyConfig))]
internal partial class CrunchyConfigJsonContext : JsonSerializerContext { }

internal class CrunchyConfigStore(string botName, ILogger logger)
    : CommandConfigBase<CrunchyConfig>(botName, "Crunchybot", logger)
{
    // Set before the base constructor runs, which loads the config and calls OnConfigLoaded.
    private readonly ILogger _logger = logger;

    protected override JsonSerializerContext JsonContext => CrunchyConfigJsonContext.Default;
    protected override JsonTypeInfo<CrunchyConfig> JsonTypeInfo =>
        CrunchyConfigJsonContext.Default.CrunchyConfig;

    /// <summary>Null when the pattern is empty or invalid.</summary>
    public Regex? NameMisspelling { get; private set; }

    /// <summary>Null when the pattern is empty or invalid.</summary>
    public Regex? ImpostorName { get; private set; }

    protected override CrunchyConfig CreateDefaultConfig() =>
        new()
        {
            OwnerUserId = 142829604330143744, // ggppjj
        };

    protected override void OnConfigLoaded()
    {
        NameMisspelling = BuildPattern(Config.NameMisspellingPattern, "NameMisspellingPattern");
        ImpostorName = BuildPattern(Config.ImpostorNamePattern, "ImpostorNamePattern");
    }

    public bool IsOwner(ulong userId) => Config.OwnerUserId != 0 && userId == Config.OwnerUserId;

    public bool IsAdmin(ulong userId) => IsOwner(userId) || Config.AdminUserIds.Contains(userId);

    public bool CanShodan(ulong userId) =>
        IsAdmin(userId) || Config.ShodanUserIds.Contains(userId);

    public bool Matches(Regex? pattern, string text)
    {
        if (pattern is null)
            return false;
        try
        {
            return pattern.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            _logger.Warning($"Pattern '{pattern}' timed out on a message. Treated as no match.");
            return false;
        }
    }

    private Regex? BuildPattern(string pattern, string settingName)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return null;
        try
        {
            // The timeout keeps a bad pattern in the config from stalling message handling.
            return new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException ex)
        {
            _logger.Error(ex, $"{settingName} is not a valid regex. It is off.");
            return null;
        }
    }
}
