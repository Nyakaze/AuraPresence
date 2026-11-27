using System.IO;
using System.Text.Json;
using Aura.Presence.Services.Chromium;
using Crovus.RichPresence;

namespace Aura.Presence.App.Output;

public sealed class DiscordGames(string discordStorage)
{
    private const string Origin = "https://discord.com";
    private const string StoreKey = "RunningGameStore";

    private readonly LocalStorageReader _storage = new(discordStorage);
    private Dictionary<string, string> _added = [];

    public DiscordGames() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "discord",
        "Local Storage", "leveldb"))
    {
    }

    public DetectableGames? Detectable { get; set; }

    public string? Find(string processPath)
    {
        var path = processPath.Replace('\\', '/').ToLowerInvariant();

        if ((Detectable?.Find(processPath) ?? _added.GetValueOrDefault(path)) is not { } game)
            return null;

        var off = ReadSettings();

        return off.Contains($"{path}:{game}") ? null : game;
    }

    public void Refresh() => ReadSettings();

    private HashSet<string> ReadSettings()
    {
        var added = new Dictionary<string, string>(StringComparer.Ordinal);
        var off = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            if (_storage.Read(Origin, StoreKey) is not { } json)
                return off;

            using var document = JsonDocument.Parse(json);

            if (document.RootElement.TryGetProperty("enableDetection", out var detection) && detection.ValueKind == JsonValueKind.Object)
            {
                foreach (var setting in detection.EnumerateObject().Where(setting => setting.Value.ValueKind == JsonValueKind.False))
                    off.Add(setting.Name);
            }

            if (document.RootElement.TryGetProperty("gameOverrides", out var overrides) && overrides.ValueKind == JsonValueKind.Object)
            {
                foreach (var game in overrides.EnumerateObject().Where(game => game.Value.ValueKind == JsonValueKind.Object))
                {
                    if (Flag(game.Value, "hidden"))
                        off.Add(game.Name);
                    else if (Flag(game.Value, "add") && Text(game.Value, "exePath") is { } exePath && Text(game.Value, "name") is { } name)
                        added[exePath] = name;
                }
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return off;
        }

        _added = added;

        return off;
    }

    private static bool Flag(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
