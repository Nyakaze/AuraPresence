using Aura.Presence.App.Arbitration;
using Aura.Presence.Sdk;

namespace Aura.Presence.App.Plugins;

public sealed class LoadedPlugin(IPresencePlugin plugin, PluginContext context, bool enabled)
{
    public IPresencePlugin Plugin { get; set; } = plugin;

    public PluginContext Context { get; } = context;

    public bool Enabled { get; set; } = enabled;
}

public sealed class PluginHost : IAsyncDisposable
{
    private const string DisabledPluginsSetting = "disabledPlugins";
    private const string DisabledMessage = "Disabled.";

    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly AppLog _log = AppLog.For("PluginHost");
    private readonly ActivityArbiter _arbiter;
    private readonly JsonPluginSettings _settings;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<LoadedPlugin> _plugins = [];
    private readonly MediaBus _mediaBus = new();

    public PluginHost(ActivityArbiter arbiter, JsonPluginSettings settings)
    {
        _arbiter = arbiter;
        _settings = settings;
    }

    public IReadOnlyList<LoadedPlugin> Plugins => _plugins;

    public async Task StartAsync(string pluginsDirectory)
    {
        var disabled = _settings.Get<string[]>(DisabledPluginsSetting, []).ToHashSet(StringComparer.Ordinal);

        foreach (var plugin in PluginLoader.LoadAll(pluginsDirectory))
        {
            if (_plugins.Any(loaded => loaded.Plugin.Id == plugin.Id))
            {
                _log.Warn($"Duplicate plugin id '{plugin.Id}' ignored.");
                continue;
            }

            var loaded = new LoadedPlugin(plugin, new PluginContext(plugin, _arbiter, _mediaBus),
                !disabled.Contains(plugin.Id));
            _plugins.Add(loaded);

            if (loaded.Enabled)
                await StartPluginAsync(loaded);
            else
                loaded.Context.ReportHealth(PluginHealth.Waiting, DisabledMessage);
        }
    }

    public async Task SetEnabledAsync(LoadedPlugin loaded, bool enabled)
    {
        if (loaded.Enabled == enabled)
            return;

        loaded.Enabled = enabled;
        _settings.Set(DisabledPluginsSetting,
            _plugins.Where(plugin => !plugin.Enabled).Select(plugin => plugin.Plugin.Id).ToArray());

        if (enabled)
        {
            if (Activator.CreateInstance(loaded.Plugin.GetType()) is IPresencePlugin fresh)
                loaded.Plugin = fresh;

            _log.Info($"Plugin '{loaded.Plugin.Id}' enabled.");
            await StartPluginAsync(loaded);

            return;
        }

        await StopPluginAsync(loaded);
        loaded.Context.ReportHealth(PluginHealth.Waiting, DisabledMessage);
        _log.Info($"Plugin '{loaded.Plugin.Id}' disabled.");
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();

        foreach (var loaded in _plugins.Where(plugin => plugin.Enabled))
            await StopPluginAsync(loaded);

        _shutdown.Dispose();
    }

    private async Task StartPluginAsync(LoadedPlugin loaded)
    {
        try
        {
            await loaded.Plugin.StartAsync(loaded.Context, _shutdown.Token);
        }
        catch (Exception exception)
        {
            loaded.Context.ReportHealth(PluginHealth.Failed, exception.Message);
            _log.Error($"Plugin '{loaded.Plugin.Id}' failed to start.", exception);
        }
    }

    private async Task StopPluginAsync(LoadedPlugin loaded)
    {
        using var timeout = new CancellationTokenSource(StopTimeout);

        try
        {
            await loaded.Plugin.StopAsync(timeout.Token);
        }
        catch (Exception exception)
        {
            _log.Error($"Plugin '{loaded.Plugin.Id}' failed to stop cleanly.", exception);
        }

        _arbiter.Withdraw(loaded.Plugin.Id);
        await loaded.Context.Media.PublishAsync(null);
    }
}
