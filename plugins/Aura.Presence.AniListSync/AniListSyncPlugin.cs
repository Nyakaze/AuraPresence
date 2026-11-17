using System.Text;
using Aura.Presence.Sdk;
using Aura.Presence.Services.AniList;

namespace Aura.Presence.AniListSync;

public sealed class AniListSyncPlugin : IPresencePlugin
{
    public const string ClientIdSetting = "clientId";
    public const string TokenSetting = "accessToken";
    public const string ThresholdSetting = "watchedPercent";
    public const int DefaultThreshold = 80;
    public const string PinRedirect = "https://anilist.co/api/v2/oauth/pin";

    private static readonly Uri DeveloperPage = new("https://anilist.co/settings/developer");

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private IPluginContext _context = null!;
    private CancellationTokenSource? _lifetime;
    private AniListClient _lookup = null!;
    private AniListAccount _account = null!;
    private WatchTracker _tracker = null!;
    private IDisposable? _subscription;
    private int _threshold;
    private volatile string? _accountName;
    private string? _watching;

    public string Id => "anilist-sync";

    public string DisplayName => "AniList Sync";

    public string Description => "Marks the anime episodes you watch as watched on your AniList.";

    public bool ShowsActivity => false;

    public IReadOnlyList<PluginSetting> Settings =>
    [
        new NumberSetting(ThresholdSetting, "Mark as watched at", DefaultThreshold, 50, 100, "%",
                "How much of an episode you watch before it counts.")
            { ShowWhen = settings => SetupStep(settings) is null, Card = "sync" },
        new TextSetting(TokenSetting, _accountName is { } name ? $"Signed in as {name}" : "AniList account", "",
                "Your access token. Click it to see it, or clear it to sign out.", Monospace: true, Secret: true)
            { ShowWhen = settings => SetupStep(settings) is null, Card = "account" },
        new TextSetting(ClientIdSetting, "Client ID", "",
                "The AniList client Aura signs in with. After changing it, clear the token and sign in again.", Monospace: true)
            { ShowWhen = settings => SetupStep(settings) is null, Card = "account" },
        new LinkSetting("Step 1 of 2 · Create an AniList client", _ => DeveloperPage, "Open AniList",
                $"Create a client named Aura with the redirect URL {PinRedirect}. Aura doesn't need its secret.")
            { ShowWhen = settings => SetupStep(settings) is not null },
        new TextSetting(ClientIdSetting, "Client ID", "",
                "The number AniList shows for your new client.", Monospace: true)
            { ShowWhen = settings => SetupStep(settings) is not null },
        new LinkSetting("Step 2 of 2 · Sign in to AniList", SignInUrl, "Sign in",
                "Approve the client on AniList, then paste the token it shows below.")
            { ShowWhen = settings => SetupStep(settings) == 2 },
        new TextSetting(TokenSetting, "Access token", "",
                "The long code AniList shows after you approve.", Monospace: true, Secret: true)
            { ShowWhen = settings => SetupStep(settings) == 2 }
    ];

    public Task StartAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _context = context;
        var token = context.Settings.Get(TokenSetting, "");

        if (token.Length == 0)
        {
            context.ReportHealth(PluginHealth.Waiting,
                SetupStep(context.Settings) == 2 ? "Step 2 of 2: sign in to AniList and paste the token."
                : context.Settings.Get(ClientIdSetting, "").Length > 0 ? "That client ID isn't a number. Paste the client's ID, not its secret."
                : "Step 1 of 2: create an AniList client and paste its ID.");
            return Task.CompletedTask;
        }

        _threshold = Math.Clamp(context.Settings.Get(ThresholdSetting, DefaultThreshold), 50, 100);
        _lookup = new AniListClient(_http);
        _account = new AniListAccount(_http, token);
        _tracker = new WatchTracker(_threshold);
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var lifetime = _lifetime.Token;
        context.ReportHealth(PluginHealth.Healthy, $"Marks episodes as watched at {_threshold} %.");
        _subscription = context.Media.Subscribe(update => OnMediaAsync(update, lifetime));
        _ = Task.Run(() => CheckSignInAsync(lifetime));

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();

        if (_lifetime is not null)
            await _lifetime.CancelAsync();

        _http.Dispose();
        _lifetime?.Dispose();
    }

    public static int? SetupStep(IPluginSettings settings) =>
        settings.Get(TokenSetting, "").Length > 0 ? null : ClientIdOf(settings) is null ? 1 : 2;

    public static Uri SignInUrl(IPluginSettings settings) =>
        ClientIdOf(settings) is { } clientId
            ? new Uri($"https://anilist.co/api/v2/oauth/authorize?client_id={clientId}&response_type=token")
            : DeveloperPage;

    private static string? ClientIdOf(IPluginSettings settings) =>
        settings.Get(ClientIdSetting, "") is { Length: > 0 } clientId && clientId.All(char.IsAsciiDigit) ? clientId : null;

    public static bool Accepts(string title, int? season, AniListMedia candidate, bool strict) =>
        TitleMatches(title, season, candidate.English, strict) || TitleMatches(title, season, candidate.Romaji, strict);

    public static string? StatusFor(int episode, AniListEntry entry)
    {
        if (entry.Progress >= episode || entry.Episodes < episode)
            return null;

        return entry.Episodes <= episode ? "COMPLETED" : entry.Status == "REPEATING" ? "REPEATING" : "CURRENT";
    }

    private static bool TitleMatches(string title, int? season, string? candidate, bool strict)
    {
        var wanted = Normalize(title);
        var found = Normalize(candidate);

        if (wanted.Length == 0 || found.Length == 0)
            return false;

        if (season is > 1)
            return found != wanted && (strict ? found.StartsWith(wanted, StringComparison.Ordinal) : found.Contains(wanted, StringComparison.Ordinal));

        return strict
            ? found == wanted
            : found.Contains(wanted, StringComparison.Ordinal) || wanted.Contains(found, StringComparison.Ordinal);
    }

    private static string Normalize(string? title) =>
        string.Concat((title ?? "").Normalize(NormalizationForm.FormD).Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private ValueTask OnMediaAsync(MediaUpdate update, CancellationToken token)
    {
        if (update.Media is { Kind: MediaKind.Episode } seen && $"{update.SourceId}|{seen.Title}|{seen.Season}|{seen.Episode}" is var watching &&
            watching != _watching)
        {
            _watching = watching;
            _context.Log.Info($"Following '{seen.Title}' season {seen.Season?.ToString() ?? "-"} episode {seen.Episode} from {update.SourceId}, " +
                              $"at {seen.Position:h\\:mm\\:ss} of {seen.Duration:h\\:mm\\:ss}; marks it at {_threshold} %.");
        }

        if (update.Media is { } media && _tracker.Claim(update.SourceId, media) is { } key)
            _ = Task.Run(() => SyncAsync(key, media, token));

        return ValueTask.CompletedTask;
    }

    private async Task CheckSignInAsync(CancellationToken token)
    {
        try
        {
            var (_, name) = await _account.ViewerAsync(token);
            _accountName = name;
            _context.Log.Info($"Signed in to AniList as {name}.");
            _context.ReportHealth(PluginHealth.Healthy, $"Signed in as {name}. Marks episodes as watched at {_threshold} %.");
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
        }
        catch (AniListException exception) when (exception.Unauthorized)
        {
            SignInRejected(exception);
        }
        catch (Exception exception)
        {
            _context.Log.Warn("Couldn't check the AniList sign-in.", exception);
        }
    }

    private async Task SyncAsync(string key, NowPlaying media, CancellationToken token)
    {
        var episode = media.Episode!.Value;
        var label = $"'{media.Title}'{(media.Season is { } season ? $" season {season}" : "")} episode {episode}";

        try
        {
            if (await ResolveAsync(media, token) is not { } id)
            {
                _context.Log.Info($"No AniList match for {label}; not syncing it.");
                return;
            }

            if (await _account.MediaAsync(id, token) is not { } entry)
            {
                _context.Log.Info($"AniList has no anime {id} for {label}; not syncing it.");
                return;
            }

            if (StatusFor(episode, entry) is not { } status)
            {
                _context.Log.Info($"Skipped {label} on {entry.Title} ({id}): progress {entry.Progress?.ToString() ?? "none"} of {entry.Episodes?.ToString() ?? "?"}.");
                return;
            }

            await _account.SaveProgressAsync(id, episode, status, token);
            _context.Log.Info($"Marked {entry.Title} ({id}) episode {episode} as watched, status {status}, from {label}.");
            _context.ReportHealth(PluginHealth.Healthy, $"Marked {entry.Title} episode {episode} as watched.");
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
        }
        catch (AniListException exception) when (exception.Unauthorized)
        {
            SignInRejected(exception);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or AniListException { Transient: true })
        {
            _tracker.Release(key);
            _context.Log.Warn($"Couldn't reach AniList for {label}; trying again on the next update.", exception);
            _context.ReportHealth(PluginHealth.Degraded, "Couldn't reach AniList; trying again.");
        }
        catch (Exception exception)
        {
            _context.Log.Error($"Syncing {label} failed.", exception);
            _context.ReportHealth(PluginHealth.Degraded, "Couldn't sync the last episode; check the log.");
        }
    }

    private async Task<int?> ResolveAsync(NowPlaying media, CancellationToken token)
    {
        if (media.Ids.TryGetValue(MediaIdKeys.AniList, out var aniList) && int.TryParse(aniList, out var aniListId))
            return aniListId;

        if (media.Ids.TryGetValue(MediaIdKeys.MyAnimeList, out var mal) && int.TryParse(mal, out var malId))
            return (await _lookup.FindByMalIdAsync(malId, token))?.Id;

        var search = media.Season is > 1 ? $"{media.Title} Season {media.Season}" : media.Title;
        var strict = !media.IsAnime;

        return (await _lookup.FindAnimeAsync(search, candidate => Accepts(media.Title, media.Season, candidate, strict), token))?.Id;
    }

    private void SignInRejected(Exception exception)
    {
        _subscription?.Dispose();
        _context.Log.Warn("AniList rejected the access token.", exception);
        _context.ReportHealth(PluginHealth.Failed, "AniList rejected the sign-in; sign in again.");
    }
}

public sealed class WatchTracker(int percent)
{
    private readonly Lock _gate = new();
    private readonly HashSet<string> _claimed = new(StringComparer.OrdinalIgnoreCase);

    public string? Claim(string sourceId, NowPlaying media)
    {
        if (media is not { Kind: MediaKind.Episode, Episode: { } episode, Position: { } position, Duration: { } duration } ||
            media.Season is 0 || duration <= TimeSpan.Zero || position / duration * 100 < percent)
            return null;

        var key = $"{sourceId}\n{media.Title}\n{media.Season}\n{episode}\n" +
                  string.Join(',', media.Ids.OrderBy(id => id.Key, StringComparer.Ordinal).Select(id => $"{id.Key}={id.Value}"));

        lock (_gate)
            return _claimed.Add(key) ? key : null;
    }

    public void Release(string key)
    {
        lock (_gate)
            _claimed.Remove(key);
    }
}
