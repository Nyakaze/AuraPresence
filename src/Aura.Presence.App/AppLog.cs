using Aura.Presence.Sdk;

namespace Aura.Presence.App;

public sealed class AppLog : IPluginLog
{
    private static readonly Lock FileGate = new();
    private static readonly string FilePath = Path.Combine(AppPaths.Logs, "aura.log");

    private readonly string _category;

    public AppLog(string category)
    {
        _category = category;
    }

    public static AppLog For(string category) => new(category);

    public void Write(PluginLogLevel level, string message, Exception? exception = null)
    {
        var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] [{_category}] {message}";
        if (exception is not null)
        {
            line += Environment.NewLine + exception;
        }

        lock (FileGate)
        {
            File.AppendAllText(FilePath, line + Environment.NewLine);
        }
    }
}
