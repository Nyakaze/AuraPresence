using System.Collections.Concurrent;
using System.Text.Json;

namespace Aura.Presence.Services.Cinemeta;

public enum CinemetaType
{
    Movie,
    Series
}

public sealed record CinemetaItem(string ImdbId, string Name, CinemetaType Type, string? PosterUrl, string? ReleaseInfo);

public sealed class CinemetaClient : IDisposable
{
    private const string BaseUrl = "https://v3-cinemeta.strem.io";
    private const string AnimeBaseUrl = "https://anime-kitsu.strem.fun";

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(6);

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ConcurrentDictionary<string, (CinemetaItem? Item, DateTimeOffset Expires)> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (IReadOnlyDictionary<string, string> Titles, DateTimeOffset Expires)>
        _episodeTitles = new(StringComparer.OrdinalIgnoreCase);

    public CinemetaClient(HttpClient? http = null)
    {
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    }

    public async Task<CinemetaItem?> FindAsync(string title, CinemetaType type,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

        var cacheKey = $"{type}:{title}";

        if (_cache.TryGetValue(cacheKey, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
            return cached.Item;

        var item = await SearchAsync(title, type, cancellationToken);
        _cache[cacheKey] = (item, DateTimeOffset.UtcNow + CacheLifetime);

        return item;
    }

    public async Task<string?> GetEpisodeTitleAsync(string metaId, string videoId,
        CancellationToken cancellationToken = default)
    {
        if (_episodeTitles.TryGetValue(metaId, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
            return cached.Titles.GetValueOrDefault(videoId);

        if (await LoadEpisodeTitlesAsync(metaId, cancellationToken) is not { } titles)
            return null;

        _episodeTitles[metaId] = (titles, DateTimeOffset.UtcNow + CacheLifetime);

        return titles.GetValueOrDefault(videoId);
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }

    private async Task<IReadOnlyDictionary<string, string>?> LoadEpisodeTitlesAsync(string metaId,
        CancellationToken cancellationToken)
    {
        var baseUrl = metaId.StartsWith("tt", StringComparison.Ordinal) ? BaseUrl : AnimeBaseUrl;
        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var document = JsonDocument.Parse(
                await _http.GetStreamAsync($"{baseUrl}/meta/series/{metaId}.json", cancellationToken));

            if (!document.RootElement.TryGetProperty("meta", out var meta) ||
                !meta.TryGetProperty("videos", out var videos) || videos.ValueKind is not JsonValueKind.Array)
                return titles;

            foreach (var video in videos.EnumerateArray())
            {
                if (Text(video, "id") is { } id && (Text(video, "name") ?? Text(video, "title")) is { Length: > 0 } title)
                    titles[id] = title;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            if (cancellationToken.IsCancellationRequested)
                throw;

            return null;
        }

        return titles;
    }

    public static bool NamesMatch(string searched, string candidate)
    {
        var mine = Normalize(searched);
        var theirs = Normalize(candidate);

        return mine.Length > 0 && theirs.Length > 0 && (mine.Contains(theirs) || theirs.Contains(mine));
    }

    private async Task<CinemetaItem?> SearchAsync(string title, CinemetaType type, CancellationToken cancellationToken)
    {
        var typeName = type.ToString().ToLowerInvariant();
        var url = $"{BaseUrl}/catalog/{typeName}/top/search={Uri.EscapeDataString(title)}.json";

        try
        {
            using var document = JsonDocument.Parse(await _http.GetStreamAsync(url, cancellationToken));

            if (!document.RootElement.TryGetProperty("metas", out var metas) || metas.ValueKind is not JsonValueKind.Array)
                return null;

            foreach (var meta in metas.EnumerateArray())
            {
                if (Text(meta, "id") is not { } id || !id.StartsWith("tt", StringComparison.Ordinal) ||
                    Text(meta, "name") is not { } name || !NamesMatch(title, name))
                    continue;

                return new CinemetaItem(id, name, type, Text(meta, "poster"), Text(meta, "releaseInfo"));
            }

            return null;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            if (cancellationToken.IsCancellationRequested)
                throw;

            return null;
        }
    }

    private static string Normalize(string value) =>
        string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String ? value.GetString() : null;
}
