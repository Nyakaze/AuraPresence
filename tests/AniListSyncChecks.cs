#:property TargetFramework=net10.0
#:project ../plugins/Aura.Presence.AniListSync/Aura.Presence.AniListSync.csproj
#:project ../src/Aura.Presence.Sdk/Aura.Presence.Sdk.csproj

using Aura.Presence.AniListSync;
using Aura.Presence.Sdk;
using Aura.Presence.Services.AniList;

var minutes = TimeSpan.FromMinutes(1);
var episode = new NowPlaying
{
    Title = "Frieren: Beyond Journey's End",
    Kind = MediaKind.Episode,
    Season = 1,
    Episode = 4,
    Position = 20 * minutes,
    Duration = 24 * minutes
};

var tracker = new WatchTracker(80);
Check(tracker.Claim("stremio", episode with { Position = 18 * minutes }) is null, "below the threshold does not sync");
var key = tracker.Claim("stremio", episode);
Check(key is not null, "at the threshold it syncs");
Check(tracker.Claim("stremio", episode with { Position = 23 * minutes, State = PlaybackState.Paused }) is null, "the same episode syncs only once");
Check(tracker.Claim("other", episode) is not null, "another source is its own episode");
Check(tracker.Claim("stremio", episode with { Ids = new Dictionary<string, string> { [MediaIdKeys.AniList] = "154587" } }) is not null,
    "an episode tries again once its ids are known");
Check(tracker.Claim("stremio", episode with { Episode = 5 }) is not null, "the next episode syncs");
tracker.Release(key!);
Check(tracker.Claim("stremio", episode) is not null, "a released episode can sync again");
Check(tracker.Claim("stremio", episode with { Kind = MediaKind.Movie, Episode = null }) is null, "movies never sync");
Check(tracker.Claim("stremio", episode with { Season = 0, Episode = 9 }) is null, "specials never sync");
Check(tracker.Claim("stremio", episode with { Episode = 6, Duration = null }) is null, "no duration, no sync");
Check(new WatchTracker(100).Claim("stremio", episode) is null, "the threshold setting is used");

var seasonOne = new AniListMedia(154587, AniListMediaType.Anime, "Sousou no Frieren", "Frieren: Beyond Journey’s End");
var seasonTwo = new AniListMedia(182255, AniListMediaType.Anime, "Sousou no Frieren 2nd Season", "Frieren: Beyond Journey’s End Season 2");
var spinOff = new AniListMedia(189513, AniListMediaType.Anime, "Sousou no Frieren: ●● no Mahou Part 2", null);

Check(AniListSyncPlugin.Accepts("Frieren: Beyond Journey's End", 1, seasonOne, strict: true), "strict match ignores punctuation and curly quotes");
Check(!AniListSyncPlugin.Accepts("Frieren", 1, seasonOne, strict: true), "strict match needs the whole title");
Check(AniListSyncPlugin.Accepts("Frieren", 1, seasonOne, strict: false), "anime match accepts a contained title");
Check(AniListSyncPlugin.Accepts("Frieren: Beyond Journey's End", 2, seasonTwo, strict: true), "season 2 starts with the base title");
Check(!AniListSyncPlugin.Accepts("Frieren: Beyond Journey's End", 2, seasonOne, strict: true), "season 2 never takes season 1");
Check(!AniListSyncPlugin.Accepts("Frieren: Beyond Journey's End", 2, spinOff, strict: true), "season 2 needs the base title first");
Check(AniListSyncPlugin.Accepts("Frieren", 2, seasonTwo, strict: false), "anime season 2 contains the base title");
Check(AniListSyncPlugin.Accepts("Overlord", 3, new AniListMedia(101474, AniListMediaType.Anime, "Overlord III", "Overlord III"), strict: true), "roman numeral sequels match");
Check(!AniListSyncPlugin.Accepts("Breaking Bad", null, seasonOne, strict: true), "unrelated titles never match");
Check(AniListSyncPlugin.Accepts("Pokemon", null, new AniListMedia(1, AniListMediaType.Anime, "Pokémon", null), strict: true), "accents are ignored");

Check(AniListSyncPlugin.StatusFor(4, new AniListEntry(1, "Show", 12, 3, "CURRENT")) == "CURRENT", "next episode is current");
Check(AniListSyncPlugin.StatusFor(4, new AniListEntry(1, "Show", 12, null, null)) == "CURRENT", "first watch adds the entry");
Check(AniListSyncPlugin.StatusFor(12, new AniListEntry(1, "Show", 12, 11, "CURRENT")) == "COMPLETED", "last episode completes");
Check(AniListSyncPlugin.StatusFor(4, new AniListEntry(1, "Show", null, 3, "CURRENT")) == "CURRENT", "airing show without a total");
Check(AniListSyncPlugin.StatusFor(4, new AniListEntry(1, "Show", 12, 1, "REPEATING")) == "REPEATING", "a rewatch stays a rewatch");
Check(AniListSyncPlugin.StatusFor(4, new AniListEntry(1, "Show", 12, 4, "CURRENT")) is null, "already watched is skipped");
Check(AniListSyncPlugin.StatusFor(4, new AniListEntry(1, "Show", 12, 12, "COMPLETED")) is null, "completed is skipped");
Check(AniListSyncPlugin.StatusFor(13, new AniListEntry(1, "Show", 12, 3, "CURRENT")) is null, "an episode past the total is skipped");

var setup = new Store();
Check(AniListSyncPlugin.SetupStep(setup) == 1, "setup starts with creating a client");
setup.Set(AniListSyncPlugin.ClientIdSetting, "aB3dE5fG7hJ9kL1mN3pQ5rS7tU9vW1xY3zA5bC7d");
Check(AniListSyncPlugin.SetupStep(setup) == 1, "a pasted secret is not a client ID");
Check(AniListSyncPlugin.SignInUrl(setup).AbsolutePath == "/settings/developer", "without a client ID, sign-in opens the developer page");
setup.Set(AniListSyncPlugin.ClientIdSetting, "12345");
Check(AniListSyncPlugin.SetupStep(setup) == 2, "a client ID moves on to sign-in");
Check(AniListSyncPlugin.SignInUrl(setup).Query == "?client_id=12345&response_type=token", "sign-in uses the user's client");
setup.Set(AniListSyncPlugin.TokenSetting, "token");
Check(AniListSyncPlugin.SetupStep(setup) is null, "a token finishes setup");
setup.Set(AniListSyncPlugin.TokenSetting, "");
Check(AniListSyncPlugin.SetupStep(setup) == 2, "signing out goes back to sign-in");

Console.WriteLine("All AniList sync checks passed.");

static void Check(bool condition, string what)
{
    if (condition)
        return;

    Console.Error.WriteLine($"FAILED: {what}");
    Environment.Exit(1);
}

sealed class Store : IPluginSettings
{
    private readonly Dictionary<string, object?> _values = [];

    public T Get<T>(string key, T defaultValue) => _values.TryGetValue(key, out var value) ? (T)value! : defaultValue;

    public void Set<T>(string key, T value) => _values[key] = value;
}
