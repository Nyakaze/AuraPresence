namespace Aura.Presence.Sdk;

public static class PluginLogExtensions
{
    public static void Debug(this IPluginLog log, string message) => log.Write(PluginLogLevel.Debug, message);

    public static void Info(this IPluginLog log, string message) => log.Write(PluginLogLevel.Information, message);

    public static void Warn(this IPluginLog log, string message, Exception? exception = null) =>
        log.Write(PluginLogLevel.Warning, message, exception);

    public static void Error(this IPluginLog log, string message, Exception? exception = null) =>
        log.Write(PluginLogLevel.Error, message, exception);
}
