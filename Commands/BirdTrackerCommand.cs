using System.Drawing;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using LibMultibot;
using LibMultibot.Helper_Classes;
using LibMultibot.Interfaces;
using LibMultibot.Platforms;
using Serilog;
using SkiaSharp;

namespace MultibotCLI.Commands;

// ---- API response model (hei-bird-tracker /api/route) ----

public struct Waypoint
{
    [JsonPropertyName("t")]
    public long T { get; set; }

    [JsonPropertyName("lat")]
    public double Lat { get; set; }

    [JsonPropertyName("lon")]
    public double Lon { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }
}

public class Street
{
    [JsonPropertyName("n")]
    public string Name { get; set; } = string.Empty;

    // Each point is [lat, lon].
    [JsonPropertyName("p")]
    public double[][] Points { get; set; } = [];
}

public class Poi
{
    [JsonPropertyName("n")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("lat")]
    public double Lat { get; set; }

    [JsonPropertyName("lon")]
    public double Lon { get; set; }
}

public class Places
{
    [JsonPropertyName("streets")]
    public List<Street> Streets { get; set; } = [];

    [JsonPropertyName("pois")]
    public List<Poi> Pois { get; set; } = [];
}

public class RouteResponse
{
    [JsonPropertyName("simulate")]
    public bool Simulate { get; set; }

    // Keyed by bird name (e.g. "chichi", "roscoe").
    [JsonPropertyName("waypoints")]
    public Dictionary<string, List<Waypoint>> Waypoints { get; set; } = [];

    [JsonPropertyName("places")]
    public Places Places { get; set; } = new();
}

[JsonSourceGenerationOptions]
[JsonSerializable(typeof(RouteResponse))]
internal partial class RouteResponseJsonContext : JsonSerializerContext { }

// ---- Command config ----

public class BirdTrackerConfig
{
    public string ApiUrl { get; set; } = "https://hei-bird-tracker.onrender.com/api/route";

    // Channel the bird map is posted to on a schedule. Set via !birdwatching <channelid>;
    // null means no posts are made.
    public ulong? PostChannelId { get; set; }

    // Minutes between posts. Set via !birdwatching <interval> :<minute>.
    public double PostIntervalMinutes { get; set; } = 15;

    // A time that posts are scheduled from: every post falls a whole number of intervals
    // before or after it. Set via !birdwatching <interval> :<minute>.
    public long PostAnchorUnixMs { get; set; }

    // When the last post was made, so the schedule carries on across restarts. Also set
    // when the schedule changes, so a slot from before the change is not posted as missed.
    public long? LastPostUnixMs { get; set; }

    // Bird name -> profile photo URL. Names are matched case-insensitively against
    // the waypoint keys returned by the API.
    public Dictionary<string, string> BirdPhotoUrls { get; set; } =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["roscoe"] = "https://hei-bird-tracker.onrender.com/pets/roscoe.jpg",
            ["chichi"] = "https://hei-bird-tracker.onrender.com/pets/chichi.jpg",
        };
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(BirdTrackerConfig))]
internal partial class BirdTrackerConfigJsonContext : JsonSerializerContext { }

internal class BirdTrackerCommandConfig(string botName, string commandName, ILogger logger)
    : CommandConfigBase<BirdTrackerConfig>(botName, commandName, logger)
{
    protected override JsonSerializerContext JsonContext => BirdTrackerConfigJsonContext.Default;
    protected override JsonTypeInfo<BirdTrackerConfig> JsonTypeInfo =>
        BirdTrackerConfigJsonContext.Default.BirdTrackerConfig;

    protected override BirdTrackerConfig CreateDefaultConfig() => new();
}

// ---- Command ----

internal class BirdTrackerCommand : CommandBase
{
    public override string Name { get; } = "BirdWatching";
    public override string Description { get; } =
        "Set the channel and schedule that the bird tracker map is posted on.";
    public override List<BotPlatforms> CommandPlatforms { get; } = [BotPlatforms.Discord];
    public override BotCommandTypes CommandType { get; } = BotCommandTypes.TextCommand;
    public override bool IsAdminCommand { get; } = true;
    public override Color? EmbedColor { get; } = Color.FromArgb(255, 74, 144, 217);

    private static readonly TimeSpan MinPostInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ScheduleCheckInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FailedPostRetryDelay = TimeSpan.FromMinutes(5);

    // Gives the platforms time to connect before an overdue post goes out after a restart.
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    // Settings from the tracker site's client code. These must match it for a post to say
    // what the site says.
    private const double NearPlaceMeters = 60;
    private const long MinuteMs = 60_000;
    private const long DayMs = 86_400_000;
    private const long FixIntervalMs = 5 * MinuteMs;
    private const long HistoryDays = 90;
    private const double JitterMeters = 4;
    private const double MotionMeters = 40;
    private const double AccuracyCircleMeters = 12;

    private sealed record SiteBattery(double PerDay, double ChargeAt, double AtRelease);

    private sealed record SitePet(string Name, SKColor Color, SiteBattery? Battery);

    private static readonly Dictionary<string, SitePet> _sitePets = new()
    {
        ["chichi"] = new("Chi-Chi", new SKColor(0xDC, 0x3B, 0x28), new(3.2, 15, 92)),
        ["roscoe"] = new("Roscoe", new SKColor(0x4D, 0x7D, 0xE0), new(2.8, 20, 88)),
    };

    // Pin and tooltip sizes are the site's, scaled up since Discord shows the image smaller.
    private const float PinScale = 1.4f;
    private const float PinRadius = 23f * PinScale;
    private const float PinBorder = 3f * PinScale;
    private const float PinOutline = 2f * PinScale;
    private const float TooltipFontSize = 12f * PinScale;
    private const float TooltipPaddingX = 9f * PinScale;
    private const float TooltipHeight = 24f * PinScale;
    private const float TooltipGap = 8f * PinScale;

    // Bundled because the Linux native Skia package (NoDependencies) has no system font
    // access: without a typeface from a file, all map text draws blank with zero width.
    private static readonly SKTypeface _regularTypeface = LoadTypeface("DejaVuSans.ttf");
    private static readonly SKTypeface _boldTypeface = LoadTypeface("DejaVuSans-Bold.ttf");

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(45) };

    static BirdTrackerCommand()
    {
        // OSM tile usage policy requires an identifiable User-Agent.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MultibotCLI-BirdTracker/1.0");
    }

    // Ring colours for any bird the site does not define, assigned in encounter order.
    private static readonly SKColor[] _ringColors =
    [
        new(0xF5, 0x8A, 0x07), // orange
        new(0x2E, 0x7D, 0xD2), // blue
        new(0x2E, 0xA0, 0x43), // green
        new(0xC0, 0x39, 0x2B), // red
        new(0x8E, 0x44, 0xAD), // purple
    ];

    private readonly string _cacheDirectory = Path.Combine(
        AppContext.BaseDirectory,
        "Resources",
        "BirdTracker"
    );
    private readonly BirdTrackerCommandConfig _config;

    // CommandBase.CancellationToken is swapped for a watchdog token during Init, so the
    // long-lived schedule keeps its own reference to the shutdown token.
    private readonly CancellationToken _shutdownToken;

    // One post at a time, so a manual and a scheduled post can't both go out together.
    private readonly SemaphoreSlim _postLock = new(1, 1);
    private long _retryNotBeforeUnixMs;

    internal BirdTrackerCommand(IBot originatingBot, CancellationToken cancellationToken = default)
        : base(originatingBot, cancellationToken)
    {
        _shutdownToken = cancellationToken;
        _config = new BirdTrackerCommandConfig(originatingBot.Name, Name, _logger);
    }

    public override Task<bool> Init()
    {
        _ = RunScheduleAsync();
        return Task.FromResult(true);
    }

    public override async Task<bool> PrepareResponse()
    {
        if (!IsActive)
            return false;

        var args = MessageContext?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
        if (args.Length == 2 && args[1].Equals("now", StringComparison.OrdinalIgnoreCase))
        {
            Message = await PostNowAsync();
            return true;
        }

        // The interval may be several words: !birdwatching 1 hour 30 minutes :15
        if (
            args.Length >= 3
            && TryParseSchedule(string.Join(' ', args[1..^1]), args[^1], out var interval, out var startMinute)
        )
        {
            Message = SetSchedule(interval, startMinute);
            return true;
        }

        if (args.Length != 2 || !ulong.TryParse(args[1], out var channelId) || channelId == 0)
        {
            var currentChannel = _config.Config.PostChannelId is { } id ? $"<#{id}>" : "not set";
            Message =
                "Usage: !birdwatching <channelid> | !birdwatching <interval> :<minute> | !birdwatching now "
                + $"(currently: {currentChannel}, every {IntervalText}). "
                + "Interval examples: 15m, 2h, 1h30m, 1 day. Minimum 1 minute.";
            return true;
        }

        var previousChannelId = _config.Config.PostChannelId;
        try
        {
            _config.Config.PostChannelId = channelId;
            _config.Save();
        }
        catch (Exception ex)
        {
            _config.Config.PostChannelId = previousChannelId;
            _logger.Error(ex, "Failed to save bird watching channel {ChannelId}.", channelId);
            Message = "Could not save that channel.";
            return true;
        }

        _logger.Information("Bird watching channel set to {ChannelId}.", channelId);
        Message = $"Bird map will post to <#{channelId}> every {IntervalText}.";
        return true;
    }

    // The minute is a minute of the hour, 0 to 59, written like ":15". It is parsed here
    // because the interval parser reads ":15" as 15 hours.
    private static bool TryParseSchedule(
        string intervalText,
        string minute,
        out TimeSpan interval,
        out int startMinute
    )
    {
        startMinute = 0;
        return DurationText.TryParse(intervalText, out interval)
            && interval >= MinPostInterval
            && minute.StartsWith(':')
            && int.TryParse(minute[1..], NumberStyles.None, CultureInfo.InvariantCulture, out startMinute)
            && startMinute < 60;
    }

    private string SetSchedule(TimeSpan interval, int startMinute)
    {
        var config = _config.Config;
        var previous = (config.LastPostUnixMs, config.PostIntervalMinutes, config.PostAnchorUnixMs);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        const long hourMs = 60 * MinuteMs;
        try
        {
            // Last post time goes first, so a schedule check running now can't find a post due.
            config.LastPostUnixMs = now;
            config.PostIntervalMinutes = interval.TotalMinutes;
            config.PostAnchorUnixMs = now / hourMs * hourMs + startMinute * MinuteMs;
            _config.Save();
        }
        catch (Exception ex)
        {
            (config.LastPostUnixMs, config.PostIntervalMinutes, config.PostAnchorUnixMs) = previous;
            _logger.Error(ex, "Failed to save the bird watching schedule.");
            return "Could not save that schedule.";
        }

        _retryNotBeforeUnixMs = 0;
        _logger.Information(
            "Bird watching schedule set to every {Interval} from :{Minute:00}.",
            IntervalText,
            startMinute
        );
        var channelNote = config.PostChannelId is null ? " No channel set yet." : "";
        return $"Bird map will post every {IntervalText} from :{startMinute:00}. "
            + $"Next post at <t:{NextSlotUnixMs() / 1000}:t>.{channelNote}";
    }

    private async Task<string> PostNowAsync()
    {
        if (_config.Config.PostChannelId is not { } channelId)
            return "No channel set. Use !birdwatching <channelid> first.";

        await _postLock.WaitAsync(_shutdownToken);
        try
        {
            if (!await PostBirdMapAsync(channelId))
                return "No bird location data is available right now.";
            RecordPost();
            return $"Posted to <#{channelId}>. Next post at <t:{NextSlotUnixMs() / 1000}:t>.";
        }
        catch (Exception ex) when (!_shutdownToken.IsCancellationRequested)
        {
            _logger.Error(ex, "Failed to post bird map to {ChannelId}.", channelId);
            return "Could not post the bird map right now.";
        }
        finally
        {
            _postLock.Release();
        }
    }

    // Checks often rather than sleeping until the due time, so a restart or a config edit
    // is simply picked up by the next check. Posts are due on fixed clock slots counted from
    // the schedule's anchor, so a slow post, a retry or a restart can't shift the later ones.
    private async Task RunScheduleAsync()
    {
        try
        {
            await Task.Delay(StartupDelay, _shutdownToken);
            using var timer = new PeriodicTimer(ScheduleCheckInterval);
            do
            {
                if (IsPostDue())
                    await RunScheduledPostAsync();
            } while (await timer.WaitForNextTickAsync(_shutdownToken));
        }
        catch (OperationCanceledException) { }
    }

    private bool IsPostDue()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var lastPost = _config.Config.LastPostUnixMs ?? 0;
        return IsActive
            && _config.Config.PostChannelId is not null
            && lastPost < CurrentSlotUnixMs(now)
            && now >= _retryNotBeforeUnixMs;
    }

    // Clamped because the interval can be hand-edited in the config file.
    private long IntervalMs =>
        Math.Max((long)MinPostInterval.TotalMilliseconds, (long)Math.Round(_config.Config.PostIntervalMinutes * MinuteMs));

    private string IntervalText =>
        $"{(IntervalMs / (double)MinuteMs).ToString("0.##", CultureInfo.InvariantCulture)} minutes";

    // The latest slot at or before a time. The anchor may be later than the time, so the
    // remainder is made non-negative.
    private long CurrentSlotUnixMs(long time)
    {
        var interval = IntervalMs;
        var sinceAnchor = time - _config.Config.PostAnchorUnixMs;
        return time - (sinceAnchor % interval + interval) % interval;
    }

    private long NextSlotUnixMs() =>
        CurrentSlotUnixMs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) + IntervalMs;

    private async Task RunScheduledPostAsync()
    {
        await _postLock.WaitAsync(_shutdownToken);
        try
        {
            // A manual post may have gone out while waiting for the lock.
            if (!IsPostDue() || _config.Config.PostChannelId is not { } channelId)
                return;

            try
            {
                if (await PostBirdMapAsync(channelId))
                    RecordPost();
                else
                    DelayRetry();
            }
            catch (Exception ex) when (!_shutdownToken.IsCancellationRequested)
            {
                _logger.Error(ex, "Failed to post bird map to {ChannelId}.", channelId);
                DelayRetry();
            }
        }
        finally
        {
            _postLock.Release();
        }
    }

    private void DelayRetry() =>
        _retryNotBeforeUnixMs =
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            + (long)FailedPostRetryDelay.TotalMilliseconds;

    private void RecordPost()
    {
        _config.Config.LastPostUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        try
        {
            _config.Save();
        }
        catch (Exception ex)
        {
            // The in-memory time still prevents a repeat post until the next restart.
            _logger.Error(ex, "Failed to save the bird watching post time.");
        }
    }

    // Returns false, having posted nothing, if there is no location data to post.
    internal async Task<bool> PostBirdMapAsync(ulong channelId)
    {
        RouteResponse? route;
        await using (var stream = await _http.GetStreamAsync(_config.Config.ApiUrl, CancellationToken))
        {
            route = await JsonSerializer.DeserializeAsync(
                stream,
                RouteResponseJsonContext.Default.RouteResponse,
                CancellationToken
            );
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var birds = route is null ? [] : EvaluateBirds(route, now);
        var onMap = birds.Where(b => b.Last is not null).ToList();
        if (onMap.Count == 0)
        {
            _logger.Warning("No bird location data available; skipping post.");
            return false;
        }

        var outputPath = await RenderMapAsync(onMap, now);
        try
        {
            await OriginatingBot.SendEmbed(
                channelId,
                "Bird Tracker: Current Locations",
                string.Join("\n\n", birds.Select(b => DescribeBird(b, now))),
                EmbedColor,
                outputPath,
                "birdmap.png"
            );
            return true;
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    // ---- Tracker site model ----
    // Everything below up to the map drawing is a port of the tracker site's client code, so
    // that a post says what the site's cards say at the same moment. The site derives its
    // fixes, jitter, distance, battery and place label in the browser from /api/route.

    private enum BirdState
    {
        NoData,
        Pending,
        Live,
        Offline,
    }

    private readonly record struct Fix(long T, double Lat, double Lon);

    private sealed class BirdStatus
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required SKColor Color { get; init; }
        public BirdState State { get; init; }
        public Fix? Last { get; init; }
        public double DistanceToday { get; init; }
        public int? Battery { get; init; }
        public bool BatteryDied { get; init; }
        public string? Place { get; init; }
    }

    private List<BirdStatus> EvaluateBirds(RouteResponse route, long now)
    {
        // Known birds first, in the site's order.
        var ids = _sitePets
            .Keys.Where(route.Waypoints.ContainsKey)
            .Concat(route.Waypoints.Keys.Where(id => !_sitePets.ContainsKey(id)));

        return ids.Select((id, i) =>
            {
                var known = _sitePets.GetValueOrDefault(id);
                var pet =
                    known
                    ?? new SitePet(Capitalize(id), _ringColors[i % _ringColors.Length], null);
                var points = route.Waypoints[id].OrderBy(p => p.T).ToList();
                return EvaluateBird(id, pet, points, now, route.Places);
            })
            .ToList();
    }

    private static BirdStatus EvaluateBird(
        string id,
        SitePet pet,
        List<Waypoint> points,
        long now,
        Places places
    )
    {
        if (points.Count == 0)
            return new BirdStatus { Id = id, Name = pet.Name, Color = pet.Color, State = BirdState.NoData };
        if (now < points[0].T)
            return new BirdStatus { Id = id, Name = pet.Name, Color = pet.Color, State = BirdState.Pending };

        // The bird was released at its last waypoint before first moving over 30 m, and
        // its simulated battery counts down from then.
        var firstMove = points.FindIndex(p =>
            DistanceMeters(p.Lat, p.Lon, points[0].Lat, points[0].Lon) > 30
        );
        var releasedAt = firstMove > 0 ? points[firstMove - 1].T : points[^1].T;
        var battery = pet.Battery;
        var batteryDiesAt =
            battery is { PerDay: > 0 } b ? releasedAt + b.AtRelease / b.PerDay * DayMs : double.PositiveInfinity;
        var offlineWaypoint = points.FindIndex(p => p.Status == "offline");
        var offlineWaypointAt = offlineWaypoint >= 0 ? points[offlineWaypoint].T : double.PositiveInfinity;
        var offlineAt = Math.Min(offlineWaypointAt, batteryDiesAt);
        // Never later than now: a position is only ever calculated up to the current moment.
        var effectiveTime = (long)Math.Min(now, offlineAt);

        var fixes = new List<Fix>();
        var distanceToday = 0.0;
        Fix? previousToday = null;
        var dayAgo = now - DayMs;
        var weekAgo = now - 7 * DayMs;

        void AddFix(long t, Waypoint position)
        {
            var (dLat, dLon) = Jitter(id, t, position.Lat);
            var fix = new Fix(t, position.Lat + dLat, position.Lon + dLon);
            fixes.Add(fix);
            if (t < dayAgo)
                return;
            if (previousToday is { } p)
            {
                var moved = DistanceMeters(p.Lat, p.Lon, fix.Lat, fix.Lon);
                if (moved >= 12)
                    distanceToday += moved;
            }
            previousToday = fix;
        }

        var historyStart = Math.Max(points[0].T, now - HistoryDays * DayMs);
        var gridTime = (long)Math.Ceiling((double)historyStart / FixIntervalMs) * FixIntervalMs;
        if (gridTime <= effectiveTime)
        {
            var previous = PositionAt(points, gridTime);
            AddFix(gridTime, previous);
            while (gridTime + FixIntervalMs <= effectiveTime)
            {
                var nextTime = gridTime + FixIntervalMs;
                var next = PositionAt(points, nextTime);
                var moving =
                    DistanceMeters(previous.Lat, previous.Lon, next.Lat, next.Lon) > MotionMeters;
                if (moving)
                    for (var t = gridTime + MinuteMs; t < nextTime; t += MinuteMs)
                        AddFix(t, PositionAt(points, t));
                if (moving || nextTime >= weekAgo || nextTime % (30 * MinuteMs) == 0)
                    AddFix(nextTime, next);
                previous = next;
                gridTime = nextTime;
            }

            var lastMinute = effectiveTime / MinuteMs * MinuteMs;
            if (lastMinute > gridTime)
            {
                var latest = PositionAt(points, lastMinute);
                if (DistanceMeters(previous.Lat, previous.Lon, latest.Lat, latest.Lon) > MotionMeters)
                    for (var t = gridTime + MinuteMs; t <= lastMinute; t += MinuteMs)
                        AddFix(t, PositionAt(points, t));
            }
        }

        if (fixes.Count == 0)
        {
            var t = effectiveTime / FixIntervalMs * FixIntervalMs;
            AddFix(t, PositionAt(points, t));
        }

        var last = fixes[^1];
        var offline = now >= offlineAt;
        var note = PositionAt(points, effectiveTime).Note;
        return new BirdStatus
        {
            Id = id,
            Name = pet.Name,
            Color = pet.Color,
            State = offline ? BirdState.Offline : BirdState.Live,
            Last = last,
            DistanceToday = distanceToday,
            Battery = offline || battery is null ? null : BatteryAt(battery, effectiveTime, releasedAt),
            BatteryDied = offline && batteryDiesAt <= offlineWaypointAt,
            Place = string.IsNullOrEmpty(note) ? PlaceName(places, last.Lat, last.Lon) : note,
        };
    }

    // Interpolated position at a time; clamped to the first and last waypoints. Note comes
    // from the waypoint at or before the time.
    private static Waypoint PositionAt(List<Waypoint> points, long time)
    {
        if (time <= points[0].T)
            return points[0];
        if (time >= points[^1].T)
            return points[^1];

        int low = 0,
            high = points.Count - 1;
        while (high - low > 1)
        {
            var mid = (low + high) >> 1;
            if (points[mid].T <= time)
                low = mid;
            else
                high = mid;
        }

        var prev = points[low];
        var next = points[high];
        var fraction = (double)(time - prev.T) / (next.T - prev.T);
        return prev with
        {
            T = time,
            Lat = prev.Lat + (next.Lat - prev.Lat) * fraction,
            Lon = prev.Lon + (next.Lon - prev.Lon) * fraction,
        };
    }

    // Repeatable GPS-style noise, seeded by bird and time, so a given fix is always the same.
    private static (double DLat, double DLon) Jitter(string id, long time, double lat)
    {
        var a = Math.Max(1e-9, SeededRandom($"{id}:{time}:a"));
        var b = SeededRandom($"{id}:{time}:b");
        var magnitude = Math.Min(3, Math.Sqrt(-2 * Math.Log(a)));
        var eastMeters = magnitude * Math.Cos(2 * Math.PI * b) * JitterMeters;
        var northMeters = magnitude * Math.Sin(2 * Math.PI * b) * JitterMeters;
        return (northMeters / 111320, eastMeters / (111320 * Math.Cos(lat * Math.PI / 180)));
    }

    private static double SeededRandom(string key)
    {
        unchecked
        {
            var hash = 0x811c9dc5u;
            foreach (var c in key)
            {
                hash ^= c;
                hash *= 0x1000193u;
            }
            hash ^= hash >> 13;
            hash *= 0x5bd1e995u;
            hash ^= hash >> 15;
            return hash / 4294967296.0;
        }
    }

    private static int BatteryAt(SiteBattery battery, long time, long releasedAt)
    {
        double level;
        if (time >= releasedAt)
        {
            level = battery.AtRelease - battery.PerDay * (time - releasedAt) / DayMs;
        }
        else
        {
            // Before release it cycles between its charge threshold and full.
            var unwound = battery.AtRelease + battery.PerDay * (releasedAt - time) / DayMs;
            var range = 100 - battery.ChargeAt;
            level = battery.ChargeAt + ((unwound - battery.ChargeAt) % range + range) % range;
        }
        return (int)Math.Max(1, Math.Min(100, Math.Floor(level + 0.5)));
    }

    // The closest POI within range, otherwise the closest street (or two, at a junction),
    // otherwise the town.
    private static string PlaceName(Places places, double lat, double lon)
    {
        string? closestPoi = null;
        var closestPoiMeters = double.PositiveInfinity;
        foreach (var poi in places.Pois)
        {
            var meters = DistanceMeters(lat, lon, poi.Lat, poi.Lon);
            if (meters <= NearPlaceMeters && meters < closestPoiMeters)
            {
                closestPoi = poi.Name;
                closestPoiMeters = meters;
            }
        }
        if (closestPoi is not null)
            return closestPoi;

        var metersPerLonDegree = 111320 * Math.Cos(lat * Math.PI / 180);
        var streets = new Dictionary<string, double>();
        foreach (var street in places.Streets)
        {
            var closest = double.PositiveInfinity;
            for (var i = 1; i < street.Points.Length; i++)
            {
                closest = Math.Min(
                    closest,
                    DistanceToSegment(lat, lon, street.Points[i - 1], street.Points[i], metersPerLonDegree)
                );
            }
            if (closest < streets.GetValueOrDefault(street.Name, double.PositiveInfinity))
                streets[street.Name] = closest;
        }

        var nearest = streets.OrderBy(s => s.Value).Take(2).ToList();
        if (nearest.Count == 0 || nearest[0].Value > 300)
            return "Lake Havasu City, AZ";
        return nearest.Count > 1 && nearest[0].Value < 45 && nearest[1].Value < 45
            ? $"{nearest[0].Key} & {nearest[1].Key}"
            : nearest[0].Key;
    }

    private static double DistanceToSegment(
        double lat,
        double lon,
        double[] start,
        double[] end,
        double metersPerLonDegree
    )
    {
        var px = (lon - start[1]) * metersPerLonDegree;
        var py = (lat - start[0]) * 111320;
        var sx = (end[1] - start[1]) * metersPerLonDegree;
        var sy = (end[0] - start[0]) * 111320;
        var lengthSquared = sx * sx + sy * sy;
        var along = Math.Clamp(lengthSquared != 0 ? (px * sx + py * sy) / lengthSquared : 0, 0, 1);
        return Math.Sqrt(Math.Pow(px - along * sx, 2) + Math.Pow(py - along * sy, 2));
    }

    private static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double toRadians = Math.PI / 180.0;
        var dLat = (lat2 - lat1) * toRadians;
        var dLon = (lon2 - lon1) * toRadians;
        var a =
            Math.Pow(Math.Sin(dLat / 2), 2)
            + Math.Cos(lat1 * toRadians) * Math.Cos(lat2 * toRadians) * Math.Pow(Math.Sin(dLon / 2), 2);
        return 12742000.0 * Math.Asin(Math.Sqrt(a));
    }

    // ---- Site text ----

    // The same lines as the bird's card and popup on the site.
    private static string DescribeBird(BirdStatus bird, long now)
    {
        switch (bird.State)
        {
            case BirdState.NoData:
                return $"**{bird.Name}** · NO DATA\nNo location history";
            case BirdState.Pending:
                return $"**{bird.Name}** · NO SIGNAL\nTracker not activated yet";
        }

        var last = bird.Last!.Value;
        var meta =
            bird.State == BirdState.Offline
                ? (bird.BatteryDied ? "Battery died · " : "") + "Last seen " + DayAndTime(last.T, now)
                : "Updated "
                    + TimeAgo(last.T, now)
                    + (string.IsNullOrEmpty(bird.Place) ? "" : " · " + bird.Place);
        var tag = bird.State == BirdState.Offline ? "OFFLINE" : "LIVE";
        var battery = bird.Battery is { } level ? $"{level}%" : "Offline";
        var accuracy = 8 + (int)Math.Floor(9 * SeededRandom(bird.Id + last.T) + 0.5);
        return $"**{bird.Name}** · {tag}\n"
            + $"{meta}\n"
            + $"{FormatDistance(bird.DistanceToday)} today · Battery {battery}\n"
            + $"{last.Lat.ToString("F5", CultureInfo.InvariantCulture)}, "
            + $"{last.Lon.ToString("F5", CultureInfo.InvariantCulture)} · ±{accuracy} m";
    }

    private static string FormatDistance(double meters)
    {
        var miles = meters / 1609.344;
        return miles < 0.1
            ? $"{Math.Floor(3.28084 * meters + 0.5).ToString("F0", CultureInfo.InvariantCulture)} ft"
            : $"{miles.ToString(miles < 10 ? "F2" : "F1", CultureInfo.InvariantCulture)} mi";
    }

    private static string TimeAgo(long time, long now)
    {
        var elapsed = Math.Max(0, now - time);
        if (elapsed < 45_000)
            return "Just now";
        if (elapsed < 60 * MinuteMs)
            return $"{(long)Math.Floor(elapsed / (double)MinuteMs + 0.5)} min ago";
        if (elapsed < DayMs)
        {
            var hours = elapsed / (60 * MinuteMs);
            return $"{hours} hr{(hours > 1 ? "s" : "")} ago";
        }
        return DayAndTime(time, now);
    }

    private static string DayAndTime(long time, long now)
    {
        var key = SiteTime(time).Date;
        var day =
            key == SiteTime(now).Date ? "Today"
            : key == SiteTime(now - DayMs).Date ? "Yesterday"
            : SiteTime(time).ToString("MMM d", CultureInfo.InvariantCulture);
        return $"{day} at {ClockTime(time)}";
    }

    // The site shows times in America/Phoenix, which is MST all year.
    private static DateTimeOffset SiteTime(long time) =>
        DateTimeOffset.FromUnixTimeMilliseconds(time).ToOffset(TimeSpan.FromHours(-7));

    private static string ClockTime(long time) =>
        SiteTime(time).ToString("h:mm tt", CultureInfo.InvariantCulture) + " MST";

    private static string TooltipTime(BirdStatus bird, long now)
    {
        if (bird.State == BirdState.Offline)
            return "offline";
        var time = bird.Last!.Value.T;
        return SiteTime(time).Date == SiteTime(now).Date
            ? ClockTime(time)
            : SiteTime(time).ToString("MMM d", CultureInfo.InvariantCulture) + ", " + ClockTime(time);
    }

    // ---- Map ----

    private async Task<string> RenderMapAsync(List<BirdStatus> birds, long now)
    {
        // --- Compute geographic bounds over current bird points only. ---
        double latMin = double.MaxValue,
            latMax = double.MinValue,
            lonMin = double.MaxValue,
            lonMax = double.MinValue;

        foreach (var bird in birds)
        {
            var last = bird.Last!.Value;
            latMin = Math.Min(latMin, last.Lat);
            latMax = Math.Max(latMax, last.Lat);
            lonMin = Math.Min(lonMin, last.Lon);
            lonMax = Math.Max(lonMax, last.Lon);
        }

        // --- Web Mercator OpenStreetMap tile basemap, cached on disk. ---
        // Fixed-size view centred on the birds, at street level unless they are too far
        // apart to fit, in which case zoom out just enough to keep them all in frame.
        const int width = 1024;
        const int height = 768;
        const int tile = 256;
        const int streetZoom = 19; // highest zoom tile.openstreetmap.org serves
        const int minZoom = 10;
        const double pinMargin = 130; // room for pin bubbles and tooltips at the view edge

        var zoom = streetZoom;
        for (; zoom > minZoom; zoom--)
        {
            var spanX = (LonToTileX(lonMax, zoom) - LonToTileX(lonMin, zoom)) * tile;
            var spanY = (LatToTileY(latMin, zoom) - LatToTileY(latMax, zoom)) * tile; // north edge = smaller y
            if (spanX <= width - 2 * pinMargin && spanY <= height - 2 * pinMargin)
                break;
        }

        var originX = LonToTileX((lonMin + lonMax) / 2, zoom) * tile - width / 2.0;
        var originY = LatToTileY((latMin + latMax) / 2, zoom) * tile - height / 2.0;
        var xT0 = (int)Math.Floor(originX / tile);
        var xT1 = (int)Math.Floor((originX + width - 1) / tile);
        var yT0 = (int)Math.Floor(originY / tile);
        var yT1 = (int)Math.Floor((originY + height - 1) / tile);

        SKPoint Project(double lat, double lon) =>
            new(
                (float)(LonToTileX(lon, zoom) * tile - originX),
                (float)(LatToTileY(lat, zoom) * tile - originY)
            );

        // --- Draw. ---
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(0xE8, 0xE8, 0xE8));

        for (var tx = xT0; tx <= xT1; tx++)
            for (var ty = yT0; ty <= yT1; ty++)
            {
                var bmp = await GetTileAsync(zoom, tx, ty);
                if (bmp is null)
                    continue;
                canvas.DrawBitmap(
                    bmp,
                    (float)(tx * tile - originX),
                    (float)(ty * tile - originY),
                    SKSamplingOptions.Default,
                    null
                );
                bmp.Dispose();
            }

        // OpenStreetMap attribution (required by the tile usage policy).
        using (var attrBg = new SKPaint { Color = new SKColor(255, 255, 255, 180), IsAntialias = true })
            canvas.DrawRect(0, height - 20f, 232f, 20f, attrBg);
        using (var attr = new SKPaint { Color = SKColors.Black, IsAntialias = true })
        using (var attrFont = new SKFont(_regularTypeface, 12f))
            canvas.DrawText("© OpenStreetMap contributors", 4f, height - 6f, SKTextAlign.Left, attrFont, attr);

        // --- Bird pins with circular profile photos. ---
        // Anchors are the true locations; bubbles (the photo circles) start just above
        // the anchor and are then pushed apart so overlapping birds stay readable, while
        // a pointed tail keeps each bubble's tip on its real location.
        using var nameFont = new SKFont(_boldTypeface, TooltipFontSize);
        using var timeFont = new SKFont(_regularTypeface, TooltipFontSize);
        var anchors = birds.Select(b => Project(b.Last!.Value.Lat, b.Last.Value.Lon)).ToList();
        var tooltipWidths = birds
            .Select(b => TooltipWidth(b.Name, TooltipTime(b, now), nameFont, timeFont))
            .ToList();
        var bubbles = ResolveBubblePositions(anchors, tooltipWidths, width, height);

        var metersPerPixel =
            156543.03392 * Math.Cos((latMin + latMax) / 2 * Math.PI / 180) / (1 << zoom);

        // The site's first bird sits on top, so draw in reverse.
        for (var i = birds.Count - 1; i >= 0; i--)
        {
            var photo = await GetBirdPhotoAsync(birds[i].Id);
            DrawBirdPin(
                canvas,
                anchors[i],
                bubbles[i],
                birds[i],
                TooltipTime(birds[i], now),
                photo,
                nameFont,
                timeFont,
                (float)(AccuracyCircleMeters / metersPerPixel)
            );
            photo?.Dispose();
        }

        var dir = Path.GetTempPath();
        var outputPath = Path.Combine(dir, $"birdmap_{Guid.NewGuid():N}.png");
        using (var image = surface.Snapshot())
        using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
        await using (var fs = File.Create(outputPath))
        {
            data.SaveTo(fs);
        }
        return outputPath;
    }

    private static SKTypeface LoadTypeface(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Resources", "Fonts", fileName);
        var typeface = SKTypeface.FromFile(path);
        if (typeface is null)
            Log.Error("Could not load font {Path}; map text may be blank.", path);
        return typeface ?? SKTypeface.Default;
    }

    private static float TooltipWidth(string name, string time, SKFont nameFont, SKFont timeFont) =>
        nameFont.MeasureText(name) + timeFont.MeasureText($" · {time}") + 2 * TooltipPaddingX;

    // Spread pins so neither their circles nor their tooltips overlap. Each pin's
    // footprint is a box covering the bubble and the tooltip above it; overlapping
    // boxes are pushed apart along their axis of least penetration (AABB separation).
    private static List<SKPoint> ResolveBubblePositions(
        List<SKPoint> anchors,
        List<float> tooltipWidths,
        int width,
        int height
    )
    {
        const float gap = 4f; // breathing room between footprints
        const float tailLength = 24f; // gap between the bubble edge and the anchor
        var topExt = PinRadius + TooltipGap + TooltipHeight;
        var botExt = PinRadius + PinOutline;

        var bubbles = new List<SKPoint>(anchors.Count);
        var halfW = new List<float>(anchors.Count);
        for (var i = 0; i < anchors.Count; i++)
        {
            // Seed directly above the anchor, keeping the birds' true relative positions
            // so that separated bubbles stay on their own side and tails do not cross.
            bubbles.Add(new SKPoint(anchors[i].X, anchors[i].Y - PinRadius - tailLength));
            halfW.Add(Math.Max(PinRadius + PinOutline, tooltipWidths[i] / 2f));
        }

        for (var iter = 0; iter < 120; iter++)
        {
            var moved = false;
            for (var i = 0; i < bubbles.Count; i++)
                for (var j = i + 1; j < bubbles.Count; j++)
                {
                    var dx = bubbles[j].X - bubbles[i].X;
                    var dy = bubbles[j].Y - bubbles[i].Y;
                    // Required separation on each axis for the two footprints not to touch.
                    var needX = halfW[i] + halfW[j] + gap;
                    var needY = botExt + topExt + gap;
                    var overlapX = needX - Math.Abs(dx);
                    var overlapY = needY - Math.Abs(dy);
                    if (overlapX <= 0 || overlapY <= 0)
                        continue;

                    // Always sideways: stacking would put one pin's tooltip over the other's tail.
                    var push = overlapX / 2f;
                    var dir = dx < 0 ? -1f : 1f;
                    bubbles[i] = new SKPoint(bubbles[i].X - dir * push, bubbles[i].Y);
                    bubbles[j] = new SKPoint(bubbles[j].X + dir * push, bubbles[j].Y);
                    moved = true;
                }
            if (!moved)
                break;
        }

        // Clamp so the full footprint (bubble + tooltip) stays on the canvas.
        for (var i = 0; i < bubbles.Count; i++)
            bubbles[i] = new SKPoint(
                Math.Clamp(bubbles[i].X, halfW[i], width - halfW[i]),
                Math.Clamp(bubbles[i].Y, topExt, height - botExt)
            );

        return bubbles;
    }

    private static void DrawBirdPin(
        SKCanvas canvas,
        SKPoint anchor,
        SKPoint bubble,
        BirdStatus bird,
        string tooltipTime,
        SKBitmap? photo,
        SKFont nameFont,
        SKFont timeFont,
        float accuracyRadius
    )
    {
        var cx = bubble.X;
        var cy = bubble.Y;
        var offline = bird.State == BirdState.Offline;
        var pinColor = offline ? new SKColor(0x9C, 0xA3, 0xAF) : bird.Color;

        // Accuracy circle on the true location, like the site's.
        using (var accFill = new SKPaint { Color = bird.Color.WithAlpha(31), IsAntialias = true })
            canvas.DrawCircle(anchor, accuracyRadius, accFill);
        using (var accStroke = new SKPaint
        {
            Color = bird.Color.WithAlpha(128),
            IsStroke = true,
            StrokeWidth = PinScale,
            IsAntialias = true,
        })
            canvas.DrawCircle(anchor, accuracyRadius, accStroke);

        // Soft shadow under the bubble.
        using (var shadow = new SKPaint
        {
            Color = SKColors.Black.WithAlpha(115),
            IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 5f * PinScale),
        })
            canvas.DrawCircle(cx, cy + 3f * PinScale, PinRadius, shadow);

        // Message-bubble style tail: tapers from the ring to a sharp tip on the true
        // location, with concave, slightly lopsided sides so it curves like a speech bubble.
        SKPath? tail = null;
        var dx = anchor.X - cx;
        var dy = anchor.Y - cy;
        var len = (float)Math.Sqrt(dx * dx + dy * dy);
        if (len > PinRadius)
        {
            // Unit vector towards the tip, and its perpendicular.
            var ux = dx / len;
            var uy = dy / len;
            var px = -uy;
            var py = ux;

            const double baseAngle = 38 * Math.PI / 180;
            var along = PinRadius * (float)Math.Cos(baseAngle);
            var across = PinRadius * (float)Math.Sin(baseAngle);
            var controlAlong = PinRadius + (len - PinRadius) * 0.3f;

            using var tailBuilder = new SKPathBuilder();
            tailBuilder.MoveTo(cx + ux * along + px * across, cy + uy * along + py * across);
            tailBuilder.QuadTo(
                cx + ux * controlAlong + px * across * 0.45f,
                cy + uy * controlAlong + py * across * 0.45f,
                anchor.X,
                anchor.Y
            );
            tailBuilder.QuadTo(
                cx + ux * controlAlong - px * across * 0.05f,
                cy + uy * controlAlong - py * across * 0.05f,
                cx + ux * along - px * across,
                cy + uy * along - py * across
            );
            tailBuilder.Close();
            tail = tailBuilder.Detach();

            using var tailOutline = new SKPaint
            {
                Color = SKColors.White.WithAlpha(230),
                IsStroke = true,
                StrokeWidth = PinOutline * 2,
                StrokeJoin = SKStrokeJoin.Round,
                IsAntialias = true,
            };
            canvas.DrawPath(tail, tailOutline);
        }

        // White outline + coloured border, as on the site's marker.
        using (var outlinePaint = new SKPaint { Color = SKColors.White.WithAlpha(230), IsAntialias = true })
            canvas.DrawCircle(cx, cy, PinRadius + PinOutline, outlinePaint);
        using (var ringPaint = new SKPaint { Color = pinColor, IsAntialias = true })
        {
            if (tail is not null)
                canvas.DrawPath(tail, ringPaint);
            canvas.DrawCircle(cx, cy, PinRadius, ringPaint);
        }
        tail?.Dispose();
        using (var whitePaint = new SKPaint { Color = SKColors.White, IsAntialias = true })
            canvas.DrawCircle(cx, cy, PinRadius - PinBorder, whitePaint);

        var photoRadius = PinRadius - PinBorder;
        var dst = new SKRect(cx - photoRadius, cy - photoRadius, cx + photoRadius, cy + photoRadius);
        canvas.Save();
        using (var clipBuilder = new SKPathBuilder())
        {
            clipBuilder.AddCircle(cx, cy, photoRadius, SKPathDirection.Clockwise);
            using var clip = clipBuilder.Detach();
            canvas.ClipPath(clip, antialias: true);
        }
        if (photo is not null)
        {
            // Centre-crop the photo to a square so the circle is filled without distortion.
            var side = Math.Min(photo.Width, photo.Height);
            var src = new SKRect(
                (photo.Width - side) / 2f,
                (photo.Height - side) / 2f,
                (photo.Width + side) / 2f,
                (photo.Height + side) / 2f
            );
            var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
            using var photoPaint = new SKPaint();
            if (offline)
            {
                // Greyscale at 70% opacity, as on the site.
                photoPaint.ColorFilter = SKColorFilter.CreateColorMatrix(
                    [
                        0.2126f, 0.7152f, 0.0722f, 0, 0,
                        0.2126f, 0.7152f, 0.0722f, 0, 0,
                        0.2126f, 0.7152f, 0.0722f, 0, 0,
                        0, 0, 0, 0.7f, 0,
                    ]
                );
            }
            canvas.DrawBitmap(photo, src, dst, sampling, photoPaint);
        }
        else
        {
            using var fill = new SKPaint { Color = pinColor, IsAntialias = true };
            canvas.DrawRect(dst, fill);
        }
        canvas.Restore();

        if (offline)
            DrawOffBadge(canvas, cx, cy);

        DrawTooltip(canvas, cx, cy, bird.Name, tooltipTime, nameFont, timeFont);
    }

    private static void DrawOffBadge(SKCanvas canvas, float cx, float cy)
    {
        using var font = new SKFont(_boldTypeface, 9f * PinScale);
        var textWidth = font.MeasureText("OFF");
        var badgeWidth = textWidth + 14f * PinScale;
        var badgeHeight = 17f * PinScale;
        var right = cx + PinRadius + 6f * PinScale;
        var top = cy - PinRadius - 8f * PinScale;
        var rect = new SKRect(right - badgeWidth, top, right, top + badgeHeight);
        using var border = new SKPaint { Color = SKColors.White, IsAntialias = true };
        canvas.DrawRoundRect(rect, badgeHeight / 2, badgeHeight / 2, border);
        rect.Inflate(-2f * PinScale, -2f * PinScale);
        using var fill = new SKPaint { Color = new SKColor(0x37, 0x41, 0x51), IsAntialias = true };
        canvas.DrawRoundRect(rect, rect.Height / 2, rect.Height / 2, fill);
        using var text = new SKPaint { Color = SKColors.White, IsAntialias = true };
        canvas.DrawText("OFF", rect.MidX, rect.MidY + font.Size * 0.35f, SKTextAlign.Center, font, text);
    }

    // Dark rounded label pointing at the bubble: the site's "Name · time" tooltip.
    private static void DrawTooltip(
        SKCanvas canvas,
        float cx,
        float cy,
        string name,
        string time,
        SKFont nameFont,
        SKFont timeFont
    )
    {
        var suffix = $" · {time}";
        var tooltipWidth = TooltipWidth(name, time, nameFont, timeFont);
        var bottom = cy - PinRadius - TooltipGap;
        var rect = new SKRect(cx - tooltipWidth / 2, bottom - TooltipHeight, cx + tooltipWidth / 2, bottom);

        using var background = new SKPaint { Color = new SKColor(0x1F, 0x29, 0x37), IsAntialias = true };
        canvas.DrawRoundRect(rect, 8f * PinScale, 8f * PinScale, background);
        using (var arrowBuilder = new SKPathBuilder())
        {
            var arrow = 5f * PinScale;
            arrowBuilder.MoveTo(cx - arrow, bottom - 1f);
            arrowBuilder.LineTo(cx + arrow, bottom - 1f);
            arrowBuilder.LineTo(cx, bottom + arrow);
            arrowBuilder.Close();
            using var arrowPath = arrowBuilder.Detach();
            canvas.DrawPath(arrowPath, background);
        }

        var baseline = rect.MidY + TooltipFontSize * 0.35f;
        var nameWidth = nameFont.MeasureText(name);
        using var namePaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var timePaint = new SKPaint { Color = SKColors.White.WithAlpha(217), IsAntialias = true };
        canvas.DrawText(name, rect.Left + TooltipPaddingX, baseline, SKTextAlign.Left, nameFont, namePaint);
        canvas.DrawText(
            suffix,
            rect.Left + TooltipPaddingX + nameWidth,
            baseline,
            SKTextAlign.Left,
            timeFont,
            timePaint
        );
    }

    private async Task<SKBitmap?> GetBirdPhotoAsync(string name)
    {
        // Searched by hand: the comparer set on the default dictionary is lost when the
        // config is loaded from JSON.
        var url = _config
            .Config.BirdPhotoUrls.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Value;
        if (string.IsNullOrEmpty(url))
            return null;

        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            var cachePath = Path.Combine(_cacheDirectory, $"{SafeName(name)}.jpg");

            if (!File.Exists(cachePath))
            {
                var bytes = await _http.GetByteArrayAsync(url, CancellationToken);
                await File.WriteAllBytesAsync(cachePath, bytes, CancellationToken);
            }

            return DecodeCachedImage(cachePath);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Could not load profile photo for {Bird}.", name);
            return null;
        }
    }

    // A bad cached file is deleted so the next post downloads it again, rather than the
    // image staying missing from every post.
    private SKBitmap? DecodeCachedImage(string path)
    {
        var bitmap = SKBitmap.Decode(path);
        if (bitmap is null)
        {
            _logger.Warning("Deleting unreadable cached image {Path}.", path);
            File.Delete(path);
        }
        return bitmap;
    }

    private static double LonToTileX(double lon, int z) => (lon + 180.0) / 360.0 * (1 << z);

    private static double LatToTileY(double lat, int z)
    {
        var r = lat * Math.PI / 180.0;
        return (1.0 - Math.Log(Math.Tan(r) + 1.0 / Math.Cos(r)) / Math.PI) / 2.0 * (1 << z);
    }

    // Fetch an OSM tile, caching it on disk so each tile is requested at most once.
    private async Task<SKBitmap?> GetTileAsync(int z, int x, int y)
    {
        try
        {
            var dir = Path.Combine(_cacheDirectory, "tiles", z.ToString());
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{x}_{y}.png");
            if (!File.Exists(path))
            {
                var url = $"https://tile.openstreetmap.org/{z}/{x}/{y}.png";
                var bytes = await _http.GetByteArrayAsync(url, CancellationToken);
                await File.WriteAllBytesAsync(path, bytes, CancellationToken);
            }
            return DecodeCachedImage(path);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Could not load map tile {Z}/{X}/{Y}.", z, x, y);
            return null;
        }
    }

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string SafeName(string s)
    {
        Span<char> buffer = stackalloc char[s.Length];
        for (var i = 0; i < s.Length; i++)
            buffer[i] = char.IsLetterOrDigit(s[i]) ? s[i] : '_';
        return new string(buffer);
    }
}
