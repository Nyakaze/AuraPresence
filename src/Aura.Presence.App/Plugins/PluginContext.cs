using Aura.Presence.App.Arbitration;
using Aura.Presence.Sdk;

namespace Aura.Presence.App.Plugins;

public sealed class PluginContext : IPluginContext, IActivityReporter
{
    private readonly ActivityArbiter _arbiter;
    private readonly string _sourceId;
    private readonly int _priority;

    public PluginContext(IPresencePlugin plugin, ActivityArbiter arbiter, MediaBus mediaBus)
    {
        _arbiter = arbiter;
        _sourceId = plugin.Id;
        _priority = plugin.DefaultPriority;
        Media = mediaBus.For(plugin.Id);
        DataDirectory = AppPaths.PluginDataFor(plugin.Id);
        Log = AppLog.For(plugin.Id);
        Settings = new JsonSettings(Path.Combine(DataDirectory, "settings.json"));
    }

    public event Action<PluginHealth, string?>? HealthChanged;

    public JsonSettings Settings { get; }

    IPluginSettings IPluginContext.Settings => Settings;

    public PluginHealth Health { get; private set; } = PluginHealth.Waiting;

    public string? HealthMessage { get; private set; }

    public IActivityReporter Activity => this;

    public IMediaBus Media { get; }

    public IPluginLog Log { get; }

    public string DataDirectory { get; }

    public void ReportHealth(PluginHealth health, string? message = null)
    {
        if (Health == health && HealthMessage == message)
        {
            return;
        }

        Health = health;
        HealthMessage = message;
        Log.Info($"Health: {health}{(message is null ? "" : $" ({message})")}");
        HealthChanged?.Invoke(health, message);
    }

    public ValueTask ReportAsync(PresenceActivity activity)
    {
        _arbiter.Submit(_sourceId, _priority, activity);
        return ValueTask.CompletedTask;
    }

    public ValueTask ClearAsync()
    {
        _arbiter.Withdraw(_sourceId);
        return ValueTask.CompletedTask;
    }
}
