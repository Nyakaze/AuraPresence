using Aura.Presence.Sdk;
using Aura.Presence.Services.AniList;
using Aura.Presence.Services.Cinemeta;

namespace Aura.Presence.Stremio;

public sealed partial class StremioPlugin : IPresencePlugin
{
    public const string DefaultApplicationId = "1446922291207344189";
    public const string ApplicationIdSetting = "discordApplicationId";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RepublishInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AllowedDrift = TimeSpan.FromSeconds(3);
    private const int MissingPollsBeforeClear = 4;
    private const string UnknownTitle = "Stremio";

    private readonly StremioMediaSession _session = new();
    private readonly StremioLibrary _library = new();
    private readonly AniListClient _aniList = new();
    private readonly CinemetaClient _cinemeta = new();

    private IPluginContext _context = null!;
    private CancellationTokenSource? _lifetime;
    private Task? _loop;
    private string _applicationId = DefaultApplicationId;

    private string? _fileName;
    private ReleaseName? _release;
    private DateTimeOffset _firstSeen;
    private LibraryEntry? _entry;
    private string? _previousVideoId;
    private int _missingPolls;
    private ShownState? _shown;
    private DateTimeOffset _publishedAt;
    private string? _lookupKey;
    private volatile LookupResult? _lookup;
    private string? _episodeKey;
    private volatile string? _episodeTitle;

    public string Id => "stremio";

    public string DisplayName => "Stremio";

    public int DefaultPriority => 100;

    public Task StartAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _context = context;
        _applicationId = context.Settings.Get(ApplicationIdSetting, DefaultApplicationId);
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(() => RunAsync(_lifetime.Token), CancellationToken.None);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_lifetime is not null)
            await _lifetime.CancelAsync();

        if (_loop is not null)
            await _loop.WaitAsync(cancellationToken);

        await ClearAsync();
        _aniList.Dispose();
        _cinemeta.Dispose();
        _lifetime?.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await PollAsync();
            }
            catch (Exception exception)
            {
                _context.Log.Warn("Reading Stremio's playback failed.", exception);
                _context.ReportHealth(PluginHealth.Degraded, "Couldn't read Stremio's playback; check the log.");
            }

            try
            {
                await Task.Delay(PollInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task PollAsync()
    {
        var playback = await _session.ReadAsync();

        if (playback is null)
        {
            if (++_missingPolls >= MissingPollsBeforeClear)
            {
                _context.ReportHealth(PluginHealth.Waiting, "Nothing is playing in Stremio.");
                await ClearAsync();
            }

            return;
        }

        _missingPolls = 0;

        if (playback.FileName != _fileName)
        {
            _previousVideoId = _entry?.VideoId ?? _previousVideoId;
            _fileName = playback.FileName;
            _release = ReleaseName.Parse(playback.FileName);
            _firstSeen = DateTimeOffset.UtcNow;
            _entry = null;
            _context.Log.Info($"Now playing '{playback.FileName}', read as '{_release.Title}' S{_release.Season}E{_release.Episode}.");
            StartLookup(_release);
        }

        _context.ReportHealth(PluginHealth.Healthy, "Reading Stremio through Windows media controls.");

        if (_entry is null)
            MatchLibraryEntry();

        var media = ToNowPlaying(playback);
        var episode = EpisodeOf(media);
        StartEpisodeTitleLookup(episode);

        if (episode is { } current && current.VideoId == _episodeKey && _episodeTitle is { } episodeTitle)
            media = media with { EpisodeTitle = episodeTitle };

        await ShowAsync(media);
    }

    private void MatchLibraryEntry()
    {
        LibraryEntry? latest;

        try
        {
            latest = _library.ReadLatest();
        }
        catch (Exception exception)
        {
            _context.Log.Warn("Reading Stremio's saved library failed; showing the file name instead.", exception);

            return;
        }

        if (latest is null || !StremioLibrary.Matches(latest, _release!, _firstSeen, _previousVideoId, _lookup?.Title))
            return;

        _entry = latest;
        _context.Log.Info($"Matched '{_fileName}' to {latest.Name} ({latest.VideoId}).");
    }

    private NowPlaying ToNowPlaying(StremioPlayback playback)
    {
        var release = _release!;
        var lookup = _lookup;
        var id = _entry?.Id;
        var episode = id?.Episode ?? release.Episode;
        var kind = _entry?.Type == "movie" ? MediaKind.Movie
            : episode is not null ? MediaKind.Episode
            : _entry is not null || lookup is { IsSeries: false } ? MediaKind.Movie
            : MediaKind.Other;

        return new NowPlaying
        {
            Title = _entry?.Name ?? lookup?.Title ?? (release.HasTitle ? release.Title : UnknownTitle),
            Kind = kind,
            Season = kind is MediaKind.Episode ? id?.Season ?? release.Season : null,
            Episode = kind is MediaKind.Episode ? episode : null,
            Year = lookup?.Year,
            PosterUrl = _entry?.PosterUrl ?? lookup?.PosterUrl,
            Ids = id?.Ids ?? lookup?.Ids ?? new Dictionary<string, string>(),
            State = playback.State,
            Position = playback.Position,
            Duration = playback.Duration
        };
    }

    private bool LooksLikeAnime(NowPlaying media) => media.IsAnime || _release?.FansubStyle == true;

    private async Task ShowAsync(NowPlaying media)
    {
        var now = DateTimeOffset.UtcNow;
        var startedAt = media.State is PlaybackState.Playing && media.Position is { } position
            ? now - position
            : (DateTimeOffset?)null;
        var activity = BuildActivity(media, startedAt);

        var unchanged = _shown is { } shown && shown.Details == activity.Details && shown.State == activity.State &&
                        shown.Image == activity.LargeImage && shown.ButtonUrl == activity.Buttons[0].Url &&
                        shown.EndsAt is null == activity.EndsAt is null &&
                        (startedAt is null) == (shown.StartedAt is null) &&
                        (startedAt is null || (startedAt.Value - shown.StartedAt!.Value).Duration() < AllowedDrift);

        if (!unchanged)
        {
            await _context.Activity.ReportAsync(activity);
            _shown = new ShownState(activity.Details, activity.State, activity.LargeImage, activity.Buttons[0].Url,
                startedAt, activity.EndsAt);
        }

        if (!unchanged || now - _publishedAt >= RepublishInterval)
        {
            await _context.Media.PublishAsync(media);
            _publishedAt = now;
        }
    }

    private PresenceActivity BuildActivity(NowPlaying media, DateTimeOffset? startedAt)
    {
        var label = media.Kind switch
        {
            MediaKind.Episode when media.Season is null => $"Episode {media.Episode}",
            MediaKind.Episode => $"S{media.Season:00}E{media.Episode ?? 1:00}",
            MediaKind.Movie when media.Year is { } year => $"Movie ({year})",
            MediaKind.Movie => "Movie",
            _ => "Stremio"
        };

        var suffix = media.State switch
        {
            PlaybackState.Paused => " - Paused",
            PlaybackState.Loading => " - Loading…",
            _ => string.Empty
        };

        return new PresenceActivity
        {
            ApplicationId = _applicationId,
            Kind = ActivityKind.Watching,
            Name = media.Title,
            Details = media.Title,
            State = label + suffix,
            LargeImage = new PresenceImage(media.PosterUrl ?? "stremio", media.EpisodeTitle ?? media.Title),
            StartedAt = startedAt,
            EndsAt = startedAt is { } start && media.Duration is { } duration ? start + duration : null,
            Buttons = [ButtonFor(media)]
        };
    }

    private PresenceButton ButtonFor(NowPlaying media)
    {
        if (_lookup?.AniListUrl is { } aniListUrl)
            return new PresenceButton("View on AniList", aniListUrl);

        if (LooksLikeAnime(media))
            return new PresenceButton("View on AniList", AniListClient.SearchUrl(media.Title));

        return media.Ids.TryGetValue(MediaIdKeys.Imdb, out var imdb)
            ? new PresenceButton("View on SIMKL", new Uri($"https://api.simkl.com/redirect?to=Simkl&imdb={imdb}"))
            : new PresenceButton("View on SIMKL",
                new Uri($"https://simkl.com/search/?q={Uri.EscapeDataString(media.Title)}"));
    }

    private void StartLookup(ReleaseName release)
    {
        var key = $"{release.Title}|{release.Season}|{release.Episode is null}|{release.FansubStyle}";

        if (key == _lookupKey)
            return;

        _lookupKey = key;
        _lookup = null;

        if (!release.HasTitle)
            return;

        var token = _lifetime?.Token ?? CancellationToken.None;

        _ = Task.Run(async () =>
        {
            try
            {
                var result = release.FansubStyle ? await FromAniListAsync(release, token) : null;
                result ??= await FromCinemetaAsync(release, token);
                result ??= release.FansubStyle ? null : await FromAniListAsync(release, token);

                if (key != _lookupKey)
                    return;

                _lookup = result;
                _context.Log.Info(result is null
                    ? $"No online match for '{release.Title}'."
                    : $"Looked up '{release.Title}' as {result.Title} ({string.Join(", ", result.Ids.Select(pair => $"{pair.Key} {pair.Value}"))}).");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                _context.Log.Warn($"Looking up '{release.Title}' failed.", exception);
            }
        }, token);
    }

    private async Task<LookupResult?> FromAniListAsync(ReleaseName release, CancellationToken token)
    {
        var found = release.Season is > 1
            ? await _aniList.FindAsync($"{release.Title} Season {release.Season}", release.Title, token)
            : null;
        found ??= await _aniList.FindAsync(release.Title, token);

        if (found is null)
            return null;

        var ids = new Dictionary<string, string> { [MediaIdKeys.AniList] = found.Id.ToString() };
        var imdbMatch = await _cinemeta.FindAsync(SeasonSuffix().Replace(found.DisplayTitle, string.Empty),
            found.IsMovie ? CinemetaType.Movie : CinemetaType.Series, token);

        if (imdbMatch is not null)
            ids[MediaIdKeys.Imdb] = imdbMatch.ImdbId;

        return new LookupResult(found.DisplayTitle, found.CoverImageUrl, ids, !found.IsMovie, found.Url,
            YearOf(imdbMatch?.ReleaseInfo));
    }

    private async Task<LookupResult?> FromCinemetaAsync(ReleaseName release, CancellationToken token)
    {
        var item = release.Episode is not null
            ? await _cinemeta.FindAsync(release.Title, CinemetaType.Series, token)
            : await _cinemeta.FindAsync(release.Title, CinemetaType.Movie, token) ??
              await _cinemeta.FindAsync(release.Title, CinemetaType.Series, token);

        return item is null
            ? null
            : new LookupResult(item.Name, item.PosterUrl,
                new Dictionary<string, string> { [MediaIdKeys.Imdb] = item.ImdbId }, item.Type is CinemetaType.Series, null,
                YearOf(item.ReleaseInfo));
    }

    private void StartEpisodeTitleLookup((string MetaId, string VideoId)? episode)
    {
        if (episode is not { } current || current.VideoId == _episodeKey)
            return;

        _episodeKey = current.VideoId;
        _episodeTitle = null;
        var token = _lifetime?.Token ?? CancellationToken.None;

        _ = Task.Run(async () =>
        {
            try
            {
                var title = await _cinemeta.GetEpisodeTitleAsync(current.MetaId, current.VideoId, token);

                if (current.VideoId == _episodeKey)
                    _episodeTitle = title;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                _context.Log.Warn($"Looking up the episode title for {current.VideoId} failed.", exception);
            }
        }, token);
    }

    private static (string MetaId, string VideoId)? EpisodeOf(NowPlaying media)
    {
        if (media.Kind is not MediaKind.Episode || media.Episode is not { } episode)
            return null;

        if (media.Ids.TryGetValue(MediaIdKeys.Imdb, out var imdb) && media.Season is { } season)
            return (imdb, $"{imdb}:{season}:{episode}");

        return media.Ids.TryGetValue(MediaIdKeys.Kitsu, out var kitsu)
            ? ($"kitsu:{kitsu}", $"kitsu:{kitsu}:{episode}")
            : null;
    }

    private static int? YearOf(string? releaseInfo) =>
        releaseInfo is { Length: >= 4 } && int.TryParse(releaseInfo.AsSpan(0, 4), out var year) ? year : null;

    [System.Text.RegularExpressions.GeneratedRegex(@"\s+(?:Season\s+\d+|\d+(?:st|nd|rd|th)\s+Season)$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex SeasonSuffix();

    private async Task ClearAsync()
    {
        _previousVideoId = _entry?.VideoId ?? _previousVideoId;
        _fileName = null;
        _release = null;
        _entry = null;
        _missingPolls = 0;
        _lookupKey = null;
        _lookup = null;
        _episodeKey = null;
        _episodeTitle = null;

        if (_shown is null)
            return;

        _shown = null;
        await _context.Activity.ClearAsync();
        await _context.Media.PublishAsync(null);
    }

    private sealed record LookupResult(
        string Title,
        string? PosterUrl,
        IReadOnlyDictionary<string, string> Ids,
        bool IsSeries,
        Uri? AniListUrl,
        int? Year);

    private sealed record ShownState(
        string? Details,
        string? State,
        PresenceImage? Image,
        Uri ButtonUrl,
        DateTimeOffset? StartedAt,
        DateTimeOffset? EndsAt);
}
