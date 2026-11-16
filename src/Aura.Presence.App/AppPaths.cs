namespace Aura.Presence.App;

public static class AppPaths
{
    public static string DataRoot { get; } = EnsureDirectory(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AuraPresence"));

    public static string Logs { get; } = EnsureDirectory(Path.Combine(DataRoot, "logs"));

    public static string PluginData { get; } = EnsureDirectory(Path.Combine(DataRoot, "plugin-data"));

    public static string Plugins { get; } = Path.Combine(AppContext.BaseDirectory, "plugins");

    public static string PluginDataFor(string pluginId) => EnsureDirectory(Path.Combine(PluginData, pluginId));

    private static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
