using Aura.Presence.Sdk;

namespace Aura.Presence.App.Plugins;

public static class PluginLoader
{
    private static readonly AppLog Log = AppLog.For("PluginLoader");

    public static IReadOnlyList<IPresencePlugin> LoadAll(string pluginsDirectory)
    {
        var plugins = new List<IPresencePlugin>();
        if (!Directory.Exists(pluginsDirectory))
        {
            Log.Info($"No plugins directory at '{pluginsDirectory}'.");
            return plugins;
        }

        foreach (var pluginDirectory in Directory.EnumerateDirectories(pluginsDirectory))
        {
            var assemblyPath = Path.Combine(pluginDirectory, Path.GetFileName(pluginDirectory) + ".dll");
            if (!File.Exists(assemblyPath))
            {
                Log.Warn($"Skipping '{pluginDirectory}': expected '{Path.GetFileName(assemblyPath)}'.");
                continue;
            }

            try
            {
                plugins.AddRange(LoadFrom(assemblyPath));
            }
            catch (Exception exception)
            {
                Log.Error($"Failed to load plugin assembly '{assemblyPath}'.", exception);
            }
        }

        return plugins;
    }

    private static IEnumerable<IPresencePlugin> LoadFrom(string assemblyPath)
    {
        var context = new PluginLoadContext(assemblyPath);
        var assembly = context.LoadFromAssemblyPath(assemblyPath);

        foreach (var type in assembly.GetExportedTypes())
        {
            if (type.IsAbstract || !typeof(IPresencePlugin).IsAssignableFrom(type))
            {
                continue;
            }

            if (Activator.CreateInstance(type) is IPresencePlugin plugin)
            {
                Log.Info($"Loaded plugin '{plugin.Id}' ({plugin.DisplayName}) from '{Path.GetFileName(assemblyPath)}'.");
                yield return plugin;
            }
        }
    }
}
