using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aura.Presence.Services.AniList;

public enum AniListMediaType
{
    Anime,
    Manga
}

public sealed record AniListMedia(
    int Id,
    AniListMediaType Type,
    string? Romaji,
    string? English,
    string? CoverImageUrl = null,
    string? Format = null)
{
    public Uri Url => new($"https://anilist.co/{Type.ToString().ToLowerInvariant()}/{Id}");

    public string DisplayTitle => English ?? Romaji ?? Id.ToString();

    public bool IsMovie => Format == "MOVIE";
}

public sealed class AniListClient : IDisposable
{
    private const string MediaFields = "id type format title { romaji english } coverImage { large }";

    private const string SearchQuery = $$"""
        query ($search: String, $type: MediaType) {
          Media(search: $search, type: $type) { {{MediaFields}} }
        }
        """;

    private const string AnimeSearchQuery = $$"""
        query ($search: String) {
          Page(perPage: 10) { media(search: $search, type: ANIME) { {{MediaFields}} } }
        }
        """;

    private const string MalIdQuery = $$"""
        query ($idMal: Int) {
          Media(idMal: $idMal, type: ANIME) { {{MediaFields}} }
        }
        """;

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(6);

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ConcurrentDictionary<string, (AniListMedia? Media, DateTimeOffset Expires)> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public AniListClient(HttpClient? http = null)
    {
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    }

    public static Uri SearchUrl(string title) =>
        new($"https://anilist.co/search/anime?search={Uri.EscapeDataString(title)}");

    public Task<AniListMedia?> FindAsync(string title, CancellationToken cancellationToken = default) =>
        FindAsync(title, title, cancellationToken);

    public async Task<AniListMedia?> FindAsync(string search, string requiredTitle,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(search))
            return null;

        var cacheKey = $"{search}\n{requiredTitle}";

        if (_cache.TryGetValue(cacheKey, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
            return cached.Media;

        var media = await SearchAsync(search, requiredTitle, AniListMediaType.Anime, cancellationToken) ??
                    await SearchAsync(search, requiredTitle, AniListMediaType.Manga, cancellationToken);

        _cache[cacheKey] = (media, DateTimeOffset.UtcNow + CacheLifetime);

        return media;
    }

    public async Task<AniListMedia?> FindAnimeAsync(string search, Func<AniListMedia, bool> accepts,
        CancellationToken cancellationToken = default)
    {
        var data = await AniListGraphQl.QueryAsync(_http, AnimeSearchQuery, new JsonObject { ["search"] = search }, null,
            cancellationToken);

        return data.GetProperty("Page").GetProperty("media").EnumerateArray().Select(ToMedia).FirstOrDefault(accepts);
    }

    public async Task<AniListMedia?> FindByMalIdAsync(int malId, CancellationToken cancellationToken = default)
    {
        var data = await AniListGraphQl.QueryAsync(_http, MalIdQuery, new JsonObject { ["idMal"] = malId }, null,
            cancellationToken);

        return data.GetProperty("Media") is { ValueKind: JsonValueKind.Object } media ? ToMedia(media) : null;
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }

    public static bool TitlesMatch(string searched, string? romaji, string? english) =>
        Overlaps(searched, romaji) || Overlaps(searched, english);

    private async Task<AniListMedia?> SearchAsync(string search, string requiredTitle, AniListMediaType type,
        CancellationToken cancellationToken)
    {
        try
        {
            var data = await AniListGraphQl.QueryAsync(_http, SearchQuery,
                new JsonObject { ["search"] = search, ["type"] = type.ToString().ToUpperInvariant() }, null, cancellationToken);

            return data.GetProperty("Media") is { ValueKind: JsonValueKind.Object } found && ToMedia(found) is var media &&
                   TitlesMatch(requiredTitle, media.Romaji, media.English)
                ? media
                : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                              or AniListException or KeyNotFoundException or InvalidOperationException)
        {
            if (cancellationToken.IsCancellationRequested)
                throw;

            return null;
        }
    }

    private static AniListMedia ToMedia(JsonElement media)
    {
        var titles = media.GetProperty("title");

        return new AniListMedia(
            media.GetProperty("id").GetInt32(),
            Enum.Parse<AniListMediaType>(media.GetProperty("type").GetString() ?? nameof(AniListMediaType.Anime), ignoreCase: true),
            AniListGraphQl.Text(titles, "romaji"),
            AniListGraphQl.Text(titles, "english"),
            media.TryGetProperty("coverImage", out var cover) && cover.ValueKind is JsonValueKind.Object
                ? AniListGraphQl.Text(cover, "large")
                : null,
            AniListGraphQl.Text(media, "format"));
    }

    private static bool Overlaps(string searched, string? candidate) =>
        !string.IsNullOrWhiteSpace(candidate) &&
        (searched.Contains(candidate, StringComparison.OrdinalIgnoreCase) ||
         candidate.Contains(searched, StringComparison.OrdinalIgnoreCase));
}
