using Aura.Presence.Sdk;

namespace Aura.Presence.App.Plugins;

public static class PluginLoader
{
    private static readonly AppLog Log = AppLog.For("PluginLoader");

    public static IReadOnlyList<IPresencePlugin> LoadAll(string devDirectory, string installedDirectory)
    {
        var plugins = new List<IPresencePlugin>();

        if (Directory.Exists(devDirectory))
        {
            foreach (var pluginDirectory in Directory.EnumerateDirectories(devDirectory))
                plugins.AddRange(Load(Path.Combine(pluginDirectory, Path.GetFileName(pluginDirectory) + ".dll")));
        }

        foreach (var package in PluginStore.Cleanup(installedDirectory))
        {
            if (PluginStore.IsCompatible(package.Manifest.SdkVersion))
                plugins.AddRange(Load(package.AssemblyPath));
            else
                Log.Warn($"Skipping '{package.Manifest.InternalName}' {package.Manifest.Version}: it needs Aura API {package.Manifest.SdkVersion}, this Aura has {PluginStore.ApiVersion.ToString(2)}.");
        }

        return plugins;
    }

    public static IReadOnlyList<IPresencePlugin> Load(string assemblyPath)
    {
        if (!File.Exists(assemblyPath))
        {
            Log.Warn($"Skipping '{Path.GetDirectoryName(assemblyPath)}': expected '{Path.GetFileName(assemblyPath)}'.");
            return [];
        }

        try
        {
            return [.. LoadFrom(assemblyPath)];
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to load plugin assembly '{assemblyPath}'.", exception);
            return [];
        }
    }

    private static IEnumerable<IPresencePlugin> LoadFrom(string assemblyPath)
    {
        var context = new PluginLoadContext(assemblyPath);
        var assembly = context.LoadFromAssemblyPath(assemblyPath);

        foreach (var type in assembly.GetExportedTypes())
        {
            if (type.IsAbstract || !typeof(IPresencePlugin).IsAssignableFrom(type))
                continue;

            if (Activator.CreateInstance(type) is IPresencePlugin plugin)
            {
                Log.Info($"Loaded plugin '{plugin.Id}' ({plugin.DisplayName}) from '{assemblyPath}'.");
                yield return plugin;
            }
        }
    }
}
