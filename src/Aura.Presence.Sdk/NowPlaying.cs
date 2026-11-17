namespace Aura.Presence.Sdk;

public enum MediaKind
{
    Movie,
    Episode,
    Track,
    Other
}

public enum PlaybackState
{
    Loading,
    Playing,
    Paused
}

public static class MediaIdKeys
{
    public const string Imdb = "imdb";
    public const string Kitsu = "kitsu";
    public const string MyAnimeList = "mal";
    public const string AniList = "anilist";
    public const string AniDb = "anidb";
}

public sealed record NowPlaying
{
    public required string Title { get; init; }

    public MediaKind Kind { get; init; } = MediaKind.Other;

    public int? Season { get; init; }

    public int? Episode { get; init; }

    public string? EpisodeTitle { get; init; }

    public int? Year { get; init; }

    public string? PosterUrl { get; init; }

    public IReadOnlyDictionary<string, string> Ids { get; init; } = new Dictionary<string, string>();

    public IReadOnlyList<string> Genres { get; init; } = [];

    public PlaybackState State { get; init; } = PlaybackState.Playing;

    public TimeSpan? Position { get; init; }

    public TimeSpan? Duration { get; init; }

    public bool IsAnime =>
        Ids.ContainsKey(MediaIdKeys.Kitsu) || Ids.ContainsKey(MediaIdKeys.MyAnimeList) ||
        Ids.ContainsKey(MediaIdKeys.AniList) || Ids.ContainsKey(MediaIdKeys.AniDb) ||
        Genres.Any(genre => genre.Contains("anime", StringComparison.OrdinalIgnoreCase));
}

public sealed record MediaUpdate(string SourceId, NowPlaying? Media, DateTimeOffset At);

public interface IMediaBus
{
    ValueTask PublishAsync(NowPlaying? media);

    IDisposable Subscribe(Func<MediaUpdate, ValueTask> handler);
}
