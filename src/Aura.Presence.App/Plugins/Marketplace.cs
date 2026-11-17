using System.Net.Http;
using System.Text.Json;
using Aura.Presence.Sdk;

namespace Aura.Presence.App.Plugins;

public sealed record RepoMeta
{
    public string Name { get; init; } = "";

    public string? Url { get; init; }

    public IReadOnlyList<PluginManifest> Plugins { get; init; } = [];
}

public sealed record RepoResult(string MetaUrl, bool Official, RepoMeta? Meta, string? Error)
{
    public string Name => Meta?.Name is { Length: > 0 } name ? name : MetaUrl;
}

public sealed record CatalogEntry(PluginManifest Manifest, RepoResult Repo);

public sealed class Marketplace(JsonSettings settings)
{
    public const string OfficialUrl = "https://raw.githubusercontent.com/Nyakaze/AuraPlugins/main/meta.json";

    private const string CustomReposSetting = "customRepos";
    private const string WarningAcceptedSetting = "customReposWarningAccepted";
    private const long MaxDownload = 100 * 1024 * 1024;

    private static readonly HttpClient Http = CreateClient();

    public IReadOnlyList<string> CustomRepos => settings.Get<string[]>(CustomReposSetting, []);

    public bool WarningAccepted
    {
        get => settings.Get(WarningAcceptedSetting, false);
        set => settings.Set(WarningAcceptedSetting, value);
    }

    public async Task<IReadOnlyList<RepoResult>> FetchAsync(CancellationToken cancellationToken = default)
    {
        var official = FetchAsync(OfficialUrl, official: true, cancellationToken);
        var custom = CustomRepos.Select(url => FetchAsync(url, official: false, cancellationToken));

        return await Task.WhenAll([official, .. custom]);
    }

    public async Task<RepoResult> AddRepoAsync(string url, CancellationToken cancellationToken = default)
    {
        url = url.Trim();

        if (CheckRepoUrl(url, CustomRepos) is { } problem)
            return new RepoResult(url, false, null, problem);

        var result = await FetchAsync(url, official: false, cancellationToken);

        if (result.Meta is not null)
            settings.Set(CustomReposSetting, CustomRepos.Append(url).ToArray());

        return result;
    }

    public void RemoveRepo(string url) =>
        settings.Set(CustomReposSetting, CustomRepos.Where(existing => existing != url).ToArray());

    public static string? CheckRepoUrl(string url, IEnumerable<string> existing)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return "Paste an https:// link to a repo's meta.json.";

        if (string.Equals(url, OfficialUrl, StringComparison.OrdinalIgnoreCase))
            return "That's the official repo. It's always included.";

        return existing.Contains(url, StringComparer.OrdinalIgnoreCase) ? "That repo is already in the list." : null;
    }

    public static async Task<RepoResult> FetchAsync(string url, bool official, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.CacheControl = new() { NoCache = true };
            using var response = await Http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            return new RepoResult(url, official, Parse(await response.Content.ReadAsStringAsync(cancellationToken), url), null);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException)
        {
            AppLog.For("Marketplace").Warn($"Couldn't load the repo '{url}': {exception.Message}");
            return new RepoResult(url, official, null, Describe(exception));
        }
    }

    public static RepoMeta Parse(string json, string metaUrl)
    {
        var meta = JsonSerializer.Deserialize<RepoMeta>(json, PluginStore.Json) ?? throw new InvalidDataException("The repo file is empty.");
        var baseUri = new Uri(metaUrl);
        var log = AppLog.For("Marketplace");
        var plugins = new List<PluginManifest>();

        foreach (var plugin in meta.Plugins ?? [])
        {
            var download = Https(baseUri, plugin.DownloadUrl);

            if (!PluginStore.IsValidName(plugin.InternalName) || string.IsNullOrWhiteSpace(plugin.Name) ||
                !Version.TryParse(plugin.Version, out _) || download is null || plugin.Sha256 is not { Length: 64 })
            {
                log.Warn($"Repo '{metaUrl}' lists an invalid plugin '{plugin.InternalName}'; it was skipped.");
                continue;
            }

            plugins.Add(plugin with
            {
                DownloadUrl = download,
                IconUrl = Https(baseUri, plugin.IconUrl),
                RepoUrl = Https(baseUri, plugin.RepoUrl),
                Uses = plugin.Uses ?? [],
                InstalledFrom = null,
                ScheduledForDeletion = false
            });
        }

        return meta with { Plugins = plugins };
    }

    public static IReadOnlyList<CatalogEntry> Merge(IReadOnlyList<RepoResult> repos)
    {
        var official = repos.FirstOrDefault(repo => repo.Official);

        if (official?.Meta is null)
            return [];

        var officialNames = official.Meta.Plugins.Select(plugin => plugin.InternalName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entries = official.Meta.Plugins.Select(plugin => new CatalogEntry(plugin, official)).ToList();

        foreach (var repo in repos.Where(repo => !repo.Official && repo.Meta is not null))
        {
            foreach (var plugin in repo.Meta!.Plugins)
            {
                if (officialNames.Contains(plugin.InternalName))
                    AppLog.For("Marketplace").Warn($"Repo '{repo.MetaUrl}' tried to replace the official plugin '{plugin.InternalName}'; it was skipped.");
                else
                    entries.Add(new CatalogEntry(plugin, repo));
            }
        }

        return entries;
    }

    public static CatalogEntry? Source(PluginManifest installed, IReadOnlyList<CatalogEntry> catalog) =>
        catalog.Where(entry => string.Equals(entry.Manifest.InternalName, installed.InternalName, StringComparison.OrdinalIgnoreCase) &&
                               (entry.Repo.Official || string.Equals(entry.Repo.MetaUrl, installed.InstalledFrom, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(entry => entry.Repo.Official)
            .FirstOrDefault();

    public static CatalogEntry? UpdateFor(PluginManifest installed, IReadOnlyList<CatalogEntry> catalog, Version? api = null) =>
        Source(installed, catalog) is { } source &&
        source.Manifest.ParsedVersion > installed.ParsedVersion &&
        PluginStore.IsCompatible(source.Manifest.SdkVersion, api)
            ? source
            : null;

    public static async Task<byte[]> DownloadAsync(PluginManifest plugin, CancellationToken cancellationToken = default)
    {
        var limit = plugin.Size > 0 ? Math.Min(plugin.Size, MaxDownload) : MaxDownload;
        using var response = await Http.GetAsync(plugin.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength > limit)
            throw new InvalidDataException("The download is bigger than the repo says.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;

        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > limit)
                throw new InvalidDataException("The download is bigger than the repo says.");

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    public static string Describe(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: { } status } => $"The server answered {(int)status} {status}.",
        HttpRequestException => "Can't reach it. Check your connection.",
        TaskCanceledException => "It took too long to answer.",
        JsonException => "It isn't a valid repo file.",
        _ => exception.Message
    };

    private static string? Https(Uri baseUri, string? url) =>
        !string.IsNullOrWhiteSpace(url) && Uri.TryCreate(baseUri, url, out var resolved) && resolved.Scheme == Uri.UriSchemeHttps
            ? resolved.AbsoluteUri
            : null;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"AuraPresence/{PluginStore.ApiVersion.ToString(3)}");

        return client;
    }
}
