using System.Globalization;
using System.Text.RegularExpressions;

namespace Aura.Presence.Stremio;

public sealed partial record ReleaseName(string Title, int? Season, int? Episode, bool FansubStyle)
{
    private const int OpaqueWordLength = 20;

    private static readonly HashSet<string> VideoExtensions =
        new([".mkv", ".mp4", ".avi", ".webm", ".m4v", ".ts", ".mov", ".wmv"], StringComparer.OrdinalIgnoreCase);

    public bool HasTitle => Title.Length > 0;

    public static ReleaseName Parse(string fileName)
    {
        var name = fileName;

        for (var round = 0; round < 3 && name.Contains('%'); round++)
            name = Uri.UnescapeDataString(name);

        if (name.IndexOf('?') is var query and >= 0)
            name = QueryName(name[(query + 1)..]) ?? name[..query];

        if (VideoExtensions.Contains(Path.GetExtension(name)))
            name = name[..^Path.GetExtension(name).Length];

        var fansub = name.TrimStart().StartsWith('[');
        var stripped = Brackets().Replace(name, " ");

        int? season = null, episode = null;
        var cut = stripped.Length;

        if (SeasonEpisode().Match(stripped) is { Success: true } seasonEpisode)
        {
            season = int.Parse(seasonEpisode.Groups[1].Value, CultureInfo.InvariantCulture);
            episode = int.Parse(seasonEpisode.Groups[2].Value, CultureInfo.InvariantCulture);
            cut = seasonEpisode.Index;
        }
        else if (AnimeEpisode().Match(stripped) is { Success: true } animeEpisode)
        {
            episode = int.Parse(animeEpisode.Groups[1].Value, CultureInfo.InvariantCulture);
            cut = animeEpisode.Index;
        }
        else if (YearOrQuality().Match(stripped) is { Success: true, Index: > 0 } yearOrQuality)
        {
            cut = yearOrQuality.Index;
        }

        var title = Spacing().Replace(Separators().Replace(stripped[..cut], " "), " ").Trim(' ', '-');

        if (Words().Split(title).Any(word => word.Length >= OpaqueWordLength))
            title = string.Empty;
        else if (title == title.ToLowerInvariant())
            title = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(title);

        return new ReleaseName(title, season, episode, fansub);
    }

    public static bool TitlesMatch(string first, string second)
    {
        var mine = Normalize(first);
        var theirs = Normalize(second);

        return mine.Length > 0 && theirs.Length > 0 && (mine.Contains(theirs) || theirs.Contains(mine));
    }

    private static string? QueryName(string query) => query.Split('&')
        .Where(pair => pair.StartsWith("name=", StringComparison.OrdinalIgnoreCase))
        .Select(pair => Uri.UnescapeDataString(pair["name=".Length..].Replace('+', ' ')))
        .FirstOrDefault(value => value.Length > 0);

    private static string Normalize(string value) =>
        string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)")]
    private static partial Regex Brackets();

    [GeneratedRegex(@"\bS(\d{1,2})[ ._-]?E(\d{1,4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonEpisode();

    [GeneratedRegex(@"\s-\s(\d{1,4})(?:v\d)?\b")]
    private static partial Regex AnimeEpisode();

    [GeneratedRegex(@"\b(?:19|20)\d{2}\b|\b\d{3,4}p\b", RegexOptions.IgnoreCase)]
    private static partial Regex YearOrQuality();

    [GeneratedRegex(@"[._]+")]
    private static partial Regex Separators();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spacing();

    [GeneratedRegex(@"[\s\-]+")]
    private static partial Regex Words();
}
