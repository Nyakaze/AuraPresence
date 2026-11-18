namespace Aura.Presence.Sdk;

public interface IPresencePlugin
{
    string Id { get; }

    string DisplayName { get; }

    int DefaultPriority => 0;

    string? Description => null;

    bool ShowsActivity => true;

    IReadOnlyList<PluginSetting> Settings => [];

    Task StartAsync(IPluginContext context, CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}

public interface IPluginContext
{
    IActivityReporter Activity { get; }

    IMediaBus Media { get; }

    IBrowser Browser { get; }

    IPluginLog Log { get; }

    IPluginSettings Settings { get; }

    string DataDirectory { get; }

    void ReportHealth(PluginHealth health, string? message = null);
}

public interface IActivityReporter
{
    ValueTask ReportAsync(PresenceActivity activity);

    ValueTask ClearAsync();
}

public enum PluginHealth
{
    Healthy,
    Waiting,
    Degraded,
    Failed
}

public enum PluginLogLevel
{
    Debug,
    Information,
    Warning,
    Error
}

public interface IPluginLog
{
    void Write(PluginLogLevel level, string message, Exception? exception = null);
}

public interface IPluginSettings
{
    T Get<T>(string key, T defaultValue);

    void Set<T>(string key, T value);
}
