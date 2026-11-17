using System.Text;

namespace Aura.Presence.Services.Chromium;

public sealed class LocalStorageReader(string levelDbDirectory)
{
    private readonly Dictionary<string, string?> _values = [];
    private string _signature = string.Empty;

    public string Directory => levelDbDirectory;

    public string? Read(string origin, string key)
    {
        var signature = Signature();

        if (signature != _signature)
        {
            _values.Clear();
            _signature = signature;
        }

        var cacheKey = $"{origin}\n{key}";

        if (_values.TryGetValue(cacheKey, out var cached))
            return cached;

        var storageKey = Encoding.Latin1.GetBytes($"_{origin}\0\u0001{key}");
        LevelDbEntry? latest = null;

        foreach (var entry in LevelDb.ReadDirectory(levelDbDirectory))
        {
            if (entry.Key.AsSpan().SequenceEqual(storageKey) && (latest is null || entry.Sequence >= latest.Value.Sequence))
                latest = entry;
        }

        return _values[cacheKey] = latest?.Value is { Length: > 0 } value ? Decode(value) : null;
    }

    public static string Decode(byte[] value) => value[0] == 0
        ? Encoding.Unicode.GetString(value, 1, value.Length - 1)
        : Encoding.Latin1.GetString(value, 1, value.Length - 1);

    private string Signature()
    {
        if (!System.IO.Directory.Exists(levelDbDirectory))
            return string.Empty;

        return string.Join('|', new DirectoryInfo(levelDbDirectory).EnumerateFiles()
            .OrderBy(file => file.Name, StringComparer.Ordinal)
            .Select(file => $"{file.Name}:{file.Length}:{file.LastWriteTimeUtc.Ticks}"));
    }
}
