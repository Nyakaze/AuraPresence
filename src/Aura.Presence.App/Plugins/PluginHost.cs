using Aura.Presence.App.Arbitration;
using Aura.Presence.Sdk;

namespace Aura.Presence.App.Plugins;

public sealed class LoadedPlugin(IPresencePlugin plugin, PluginContext context, bool enabled)
{
    public IPresencePlugin Plugin { get; set; } = plugin;

    public PluginContext Context { get; } = context;

    public bool Enabled { get; set; } = enabled;

    public string Id => Plugin.Id;

    public string? Folder { get; } = PluginStore.FolderOf(plugin);

    public PluginManifest? Manifest { get; } = PluginStore.FolderOf(plugin) is { } folder ? PluginStore.Read(folder) : null;
}

public sealed class PluginHost : IAsyncDisposable
{
    private const string DisabledPluginsSetting = "disabledPlugins";
    private const string OrderSetting = "pluginOrder";
    private const string DisabledMessage = "Disabled.";

    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly AppLog _log = AppLog.For("PluginHost");
    private readonly ActivityArbiter _arbiter;
    private readonly JsonSettings _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly MediaBus _mediaBus = new();
    private List<LoadedPlugin> _plugins = [];

    public PluginHost(ActivityArbiter arbiter, JsonSettings settings)
    {
        _arbiter = arbiter;
        _settings = settings;
    }

    public event Action? Changed;

    public IReadOnlyList<LoadedPlugin> Plugins => _plugins;

    public Task StartAsync(IEnumerable<IPresencePlugin> plugins) => AddAsync(plugins);

    public Task AddAsync(IEnumerable<IPresencePlugin> plugins) => Serialized(async () =>
    {
        var disabled = _settings.Get<string[]>(DisabledPluginsSetting, []).ToHashSet(StringComparer.Ordinal);
        var order = _settings.Get<string[]>(OrderSetting, []);
        var added = new List<LoadedPlugin>();

        foreach (var plugin in plugins)
        {
            if (_plugins.Concat(added).Any(existing => existing.Id == plugin.Id))
            {
                _log.Warn($"Duplicate plugin id '{plugin.Id}' ignored.");
                continue;
            }

            var context = new PluginContext(plugin, _arbiter, _mediaBus);
            context.HealthChanged += (_, _) => Changed?.Invoke();
            added.Add(new LoadedPlugin(plugin, context, !disabled.Contains(plugin.Id)));
        }

        _plugins = [.. _plugins.Concat(added).OrderBy(plugin => Position(order, plugin.Id)).ThenByDescending(plugin => plugin.Plugin.DefaultPriority)];
        _arbiter.SetOrder(_plugins.Select(plugin => plugin.Id));

        foreach (var plugin in added)
        {
            if (plugin.Enabled)
                await StartPluginAsync(plugin);
            else
                plugin.Context.ReportHealth(PluginHealth.Waiting, DisabledMessage);
        }

        Changed?.Invoke();
    });

    public Task RemoveAsync(IReadOnlyCollection<LoadedPlugin> plugins) => Serialized(async () =>
    {
        foreach (var plugin in plugins.Where(plugin => plugin.Enabled))
            await StopPluginAsync(plugin);

        _plugins = [.. _plugins.Except(plugins)];
        _arbiter.SetOrder(_plugins.Select(plugin => plugin.Id));
        _log.Info($"Removed {string.Join(", ", plugins.Select(plugin => $"'{plugin.Id}'"))}.");
        Changed?.Invoke();
    });

    public Task SetEnabledAsync(LoadedPlugin loaded, bool enabled) => Serialized(async () =>
    {
        if (loaded.Enabled == enabled)
            return;

        loaded.Enabled = enabled;
        _settings.Set(DisabledPluginsSetting, _plugins.Where(plugin => !plugin.Enabled).Select(plugin => plugin.Id).ToArray());

        if (enabled)
        {
            Renew(loaded);
            _log.Info($"Plugin '{loaded.Id}' enabled.");
            await StartPluginAsync(loaded);
        }
        else
        {
            await StopPluginAsync(loaded);
            loaded.Context.ReportHealth(PluginHealth.Waiting, DisabledMessage);
            _log.Info($"Plugin '{loaded.Id}' disabled.");
        }

        Changed?.Invoke();
    });

    public Task RestartAsync(LoadedPlugin loaded) => Serialized(async () =>
    {
        if (!loaded.Enabled || !_plugins.Contains(loaded))
            return;

        await StopPluginAsync(loaded);
        Renew(loaded);
        _log.Info($"Plugin '{loaded.Id}' restarted.");
        await StartPluginAsync(loaded);
        Changed?.Invoke();
    });

    public void SetOrder(IEnumerable<string> pluginIds)
    {
        string[] order = [.. pluginIds];
        _plugins = [.. _plugins.OrderBy(plugin => Position(order, plugin.Id))];
        _arbiter.SetOrder(_plugins.Select(plugin => plugin.Id));
        _settings.Set(OrderSetting, _plugins.Select(plugin => plugin.Id).ToArray());
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        await _shutdown.CancelAsync();

        foreach (var loaded in _plugins.Where(plugin => plugin.Enabled))
            await StopPluginAsync(loaded);

        _shutdown.Dispose();
    }

    private static int Position(string[] order, string id)
    {
        var index = Array.IndexOf(order, id);

        return index < 0 ? int.MaxValue : index;
    }

    private static void Renew(LoadedPlugin loaded)
    {
        if (Activator.CreateInstance(loaded.Plugin.GetType()) is IPresencePlugin fresh)
            loaded.Plugin = fresh;
    }

    private async Task Serialized(Func<Task> action)
    {
        await _gate.WaitAsync();

        try
        {
            await action();
        }
        finally
        {
            _gate.Release();
        }
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
            _log.Error($"Plugin '{loaded.Id}' failed to start.", exception);
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
            _log.Error($"Plugin '{loaded.Id}' failed to stop cleanly.", exception);
        }

        _arbiter.Withdraw(loaded.Id);
        await loaded.Context.Media.PublishAsync(null);
    }
}
