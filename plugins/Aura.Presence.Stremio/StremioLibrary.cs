using System.Text.Json;
using Aura.Presence.Sdk;
using Aura.Presence.Services.Chromium;

namespace Aura.Presence.Stremio;

public sealed record LibraryEntry(
    string VideoId,
    string Name,
    string? Type,
    string? PosterUrl,
    DateTimeOffset Modified)
{
    public StremioVideoId Id => StremioVideoId.Parse(VideoId);
}

public sealed class StremioLibrary
{
    private const string Origin = "https://web.stremio.com";
    private const string Key = "library_recent";
    private const string DefaultInstallFolder = @"Programs\StremioService";
    private const string WebViewSubfolder = @"stremio-shell-ng.exe.WebView2\EBWebView\Default\Local Storage\leveldb";

    private LocalStorageReader? _reader;

    public LibraryEntry? ReadLatest()
    {
        _reader ??= new LocalStorageReader(LocateStorage());

        return Parse(_reader.Read(Origin, Key));
    }

    public static LibraryEntry? Parse(string? json)
    {
        if (json is null)
            return null;

        using var document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind is not JsonValueKind.Object)
            return null;

        LibraryEntry? latest = null;

        foreach (var item in items.EnumerateObject().Select(property => property.Value))
        {
            if (!item.TryGetProperty("state", out var state) ||
                Text(state, "video_id") is not { } videoId ||
                Text(item, "name") is not { } name ||
                !DateTimeOffset.TryParse(Text(item, "_mtime"), out var modified))
                continue;

            if (latest is null || modified > latest.Modified)
                latest = new LibraryEntry(videoId, name, Text(item, "type"), Text(item, "poster"), modified);
        }

        return latest;
    }

    public static bool Matches(LibraryEntry entry, ReleaseName release, DateTimeOffset firstSeen,
        string? previousVideoId, string? lookedUpTitle = null)
    {
        if (entry.VideoId == previousVideoId)
            return false;

        if (release.Episode is { } episode && entry.Id.Episode is { } entryEpisode && episode != entryEpisode)
            return false;

        return entry.Modified >= firstSeen - TimeSpan.FromMinutes(1) ||
               ReleaseName.TitlesMatch(release.Title, entry.Name) ||
               (lookedUpTitle is not null && ReleaseName.TitlesMatch(lookedUpTitle, entry.Name));
    }

    private static string LocateStorage()
    {
        var running = System.Diagnostics.Process.GetProcessesByName("stremio-shell-ng");

        try
        {
            var executable = running.Select(TryGetPath).FirstOrDefault(path => path is not null);

            if (executable is not null)
                return Path.Combine(Path.GetDirectoryName(executable)!, WebViewSubfolder);
        }
        finally
        {
            foreach (var process in running)
                process.Dispose();
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            DefaultInstallFolder, WebViewSubfolder);
    }

    private static string? TryGetPath(System.Diagnostics.Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String ? value.GetString() : null;
}

public sealed record StremioVideoId(IReadOnlyDictionary<string, string> Ids, int? Season, int? Episode)
{
    private static readonly HashSet<string> AnimeSchemes =
        [MediaIdKeys.Kitsu, MediaIdKeys.MyAnimeList, MediaIdKeys.AniList, MediaIdKeys.AniDb];

    public static StremioVideoId Parse(string videoId)
    {
        var parts = videoId.Split(':');

        if (AnimeSchemes.Contains(parts[0]) && parts.Length >= 2)
            return new StremioVideoId(new Dictionary<string, string> { [parts[0]] = parts[1] }, null,
                parts.Length >= 3 && int.TryParse(parts[2], out var animeEpisode) ? animeEpisode : null);

        if (parts[0].StartsWith("tt", StringComparison.Ordinal))
            return new StremioVideoId(new Dictionary<string, string> { [MediaIdKeys.Imdb] = parts[0] },
                parts.Length >= 3 && int.TryParse(parts[1], out var season) ? season : null,
                parts.Length >= 3 && int.TryParse(parts[2], out var episode) ? episode : null);

        return new StremioVideoId(new Dictionary<string, string>(), null, null);
    }
}
