using System.Collections.Concurrent;
using System.Text;
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
    private const string Endpoint = "https://graphql.anilist.co";

    private const string SearchQuery = """
        query ($search: String, $type: MediaType) {
          Media(search: $search, type: $type) { id type format title { romaji english } coverImage { large } }
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
        var request = new JsonObject
        {
            ["query"] = SearchQuery,
            ["variables"] = new JsonObject { ["search"] = search, ["type"] = type.ToString().ToUpperInvariant() }
        };

        try
        {
            using var content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(Endpoint, content, cancellationToken);

            if (!response.IsSuccessStatusCode)
                return null;

            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));

            if (!document.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("Media", out var media) || media.ValueKind is not JsonValueKind.Object)
                return null;

            var titles = media.GetProperty("title");
            var romaji = Text(titles, "romaji");
            var english = Text(titles, "english");

            return TitlesMatch(requiredTitle, romaji, english)
                ? new AniListMedia(media.GetProperty("id").GetInt32(), type, romaji, english,
                    media.TryGetProperty("coverImage", out var cover) && cover.ValueKind is JsonValueKind.Object
                        ? Text(cover, "large")
                        : null,
                    Text(media, "format"))
                : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                              or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            if (cancellationToken.IsCancellationRequested)
                throw;

            return null;
        }
    }

    private static bool Overlaps(string searched, string? candidate) =>
        !string.IsNullOrWhiteSpace(candidate) &&
        (searched.Contains(candidate, StringComparison.OrdinalIgnoreCase) ||
         candidate.Contains(searched, StringComparison.OrdinalIgnoreCase));

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String ? value.GetString() : null;
}
