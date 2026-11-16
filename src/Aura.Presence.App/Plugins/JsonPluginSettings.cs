using System.Text.Json;
using System.Text.Json.Nodes;
using Aura.Presence.Sdk;

namespace Aura.Presence.App.Plugins;

public sealed class JsonPluginSettings : IPluginSettings
{
    private readonly Lock _gate = new();
    private readonly string _filePath;
    private readonly JsonObject _values;

    public JsonPluginSettings(string filePath)
    {
        _filePath = filePath;
        _values = Load(filePath);
    }

    public T Get<T>(string key, T defaultValue)
    {
        lock (_gate)
        {
            if (!_values.TryGetPropertyValue(key, out var node) || node is null)
            {
                return defaultValue;
            }

            try
            {
                return node.Deserialize<T>() ?? defaultValue;
            }
            catch (JsonException)
            {
                return defaultValue;
            }
        }
    }

    public void Set<T>(string key, T value)
    {
        lock (_gate)
        {
            _values[key] = JsonSerializer.SerializeToNode(value);
            File.WriteAllText(_filePath, _values.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static JsonObject Load(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(filePath)) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }
}
