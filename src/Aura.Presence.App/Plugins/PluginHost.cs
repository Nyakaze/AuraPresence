using Aura.Presence.App.Arbitration;
using Aura.Presence.Sdk;

namespace Aura.Presence.App.Plugins;

public sealed record LoadedPlugin(IPresencePlugin Plugin, PluginContext Context);

public sealed class PluginHost : IAsyncDisposable
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly AppLog _log = AppLog.For("PluginHost");
    private readonly ActivityArbiter _arbiter;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<LoadedPlugin> _plugins = [];
    private readonly MediaBus _mediaBus = new();

    public PluginHost(ActivityArbiter arbiter)
    {
        _arbiter = arbiter;
    }

    public IReadOnlyList<LoadedPlugin> Plugins => _plugins;

    public async Task StartAsync(string pluginsDirectory)
    {
        foreach (var plugin in PluginLoader.LoadAll(pluginsDirectory))
        {
            if (_plugins.Any(loaded => loaded.Plugin.Id == plugin.Id))
            {
                _log.Warn($"Duplicate plugin id '{plugin.Id}' ignored.");
                continue;
            }

            var context = new PluginContext(plugin, _arbiter, _mediaBus);
            _plugins.Add(new LoadedPlugin(plugin, context));

            try
            {
                await plugin.StartAsync(context, _shutdown.Token);
            }
            catch (Exception exception)
            {
                context.ReportHealth(PluginHealth.Failed, exception.Message);
                _log.Error($"Plugin '{plugin.Id}' failed to start.", exception);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();

        using var timeout = new CancellationTokenSource(StopTimeout);
        foreach (var loaded in _plugins)
        {
            try
            {
                await loaded.Plugin.StopAsync(timeout.Token);
                _arbiter.Withdraw(loaded.Plugin.Id);
            }
            catch (Exception exception)
            {
                _log.Error($"Plugin '{loaded.Plugin.Id}' failed to stop cleanly.", exception);
            }
        }

        _shutdown.Dispose();
    }
}
