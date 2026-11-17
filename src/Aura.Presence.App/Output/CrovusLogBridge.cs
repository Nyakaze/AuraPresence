using Aura.Presence.Sdk;
using CrovusLogLevel = Crovus.Logs.LogLevel;

namespace Aura.Presence.App.Output;

public sealed class CrovusLogBridge(string category) : Crovus.Logs.ILogger
{
    private readonly AppLog _log = AppLog.For(category);

    public bool IsEnabled(CrovusLogLevel level) => level >= CrovusLogLevel.Information;

    public void Log(CrovusLogLevel level, string message, Exception? exception = null)
    {
        if (!IsEnabled(level))
            return;

        _log.Write(level switch
        {
            CrovusLogLevel.Information => PluginLogLevel.Information,
            CrovusLogLevel.Warning => PluginLogLevel.Warning,
            _ => PluginLogLevel.Error
        }, message, exception);
    }

    public Crovus.Logs.ILogger ForCategory(string childCategory) => new CrovusLogBridge($"{category}.{childCategory}");
}
