using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Aura.Presence.App.Arbitration;
using Aura.Presence.App.Output;
using Aura.Presence.App.Plugins;
using Aura.Presence.App.Tray;
using Aura.Presence.App.Ui;
using Aura.Presence.Sdk;

namespace Aura.Presence.App;

public partial class App : Application
{
    private const string InstanceName = @"Local\AuraPresence.SingleInstance";
    private const string OpenSignalName = @"Local\AuraPresence.Open";
    private const int TooltipLimit = 127;

    private readonly AppLog _log = AppLog.For("App");
    private Mutex? _instance;
    private EventWaitHandle? _openSignal;
    private RegisteredWaitHandle? _openWait;
    private TrayIcon? _tray;
    private AuraWindow? _window;
    private int _refreshQueued;
    private int _catalogRun;

    public static new App Current => (App)Application.Current;

    public ActivityArbiter Arbiter { get; private set; } = null!;

    public IPresenceOutput Output { get; private set; } = null!;

    public PluginHost Host { get; private set; } = null!;

    public Marketplace Market { get; private set; } = null!;

    public IReadOnlyList<RepoResult> Repos { get; private set; } = [];

    public IReadOnlyList<CatalogEntry> Catalog { get; private set; } = [];

    public bool CatalogLoading { get; private set; }

    public AppRelease? AppUpdate { get; private set; }

    public bool AppUpdating { get; private set; }

    public string? AppUpdateError { get; private set; }

    public static void OpenFolder(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    public string NameOf(string sourceId) => Host.Plugins.FirstOrDefault(plugin => plugin.Id == sourceId)?.Plugin.DisplayName ?? sourceId;

    public void OpenWindow()
    {
        if (_window is null)
        {
            _window = new AuraWindow();
            _window.Closed += (_, _) => _window = null;
            _window.Show();
        }

        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;

        _window.Activate();
    }

    public void TogglePin(string sourceId)
    {
        Arbiter.Pin(Arbiter.PinnedSourceId == sourceId ? null : sourceId);
        QueueRefresh();
    }

    public async Task ApplySettingAsync<T>(LoadedPlugin loaded, string key, T value)
    {
        loaded.Context.Settings.Set(key, value);
        await Host.RestartAsync(loaded);
        QueueRefresh();
    }

    public CatalogEntry? UpdateFor(LoadedPlugin loaded) =>
        loaded.Manifest is { } manifest ? Marketplace.UpdateFor(manifest, Catalog) : null;

    public async Task RefreshCatalogAsync()
    {
        var run = ++_catalogRun;
        CatalogLoading = true;
        QueueRefresh();

        try
        {
            var repos = await Market.FetchAsync();

            if (run == _catalogRun)
            {
                Repos = repos;
                Catalog = Marketplace.Merge(repos);
            }
        }
        finally
        {
            if (run == _catalogRun)
            {
                CatalogLoading = false;
                QueueRefresh();
            }
        }
    }

    public async Task CheckAppUpdateAsync()
    {
        try
        {
            AppUpdate = await AppUpdater.CheckAsync();

            if (AppUpdate is not null)
                _log.Info($"Aura {AppUpdate.Version} is available (running {AppUpdater.Current}).");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidDataException or System.Text.Json.JsonException)
        {
            _log.Warn($"Checking for an Aura update failed: {exception.Message}");
        }

        QueueRefresh();
    }

    public async Task UpdateAuraAsync()
    {
        if (AppUpdate is not { } release || AppUpdating)
            return;

        AppUpdating = true;
        AppUpdateError = null;
        QueueRefresh();

        try
        {
            await AppUpdater.DownloadAndApplyAsync(release);
            await ExitAsync(restart: true);
        }
        catch (Exception exception) when (CatalogItem.IsExpected(exception))
        {
            _log.Error($"Updating Aura to {release.Version} failed.", exception);
            AppUpdateError = Marketplace.Describe(exception);
            AppUpdating = false;
            QueueRefresh();
        }
    }

    public async Task InstallAsync(CatalogEntry entry)
    {
        var name = entry.Manifest.InternalName;
        var zip = await Marketplace.DownloadAsync(entry.Manifest);
        var folder = await Task.Run(() => PluginStore.Install(zip, entry.Manifest with { InstalledFrom = entry.Repo.MetaUrl }, AppPaths.Plugins));
        var previous = Host.Plugins.Where(plugin => string.Equals(plugin.Manifest?.InternalName, name, StringComparison.OrdinalIgnoreCase)).ToList();

        await Host.RemoveAsync(previous);
        await Host.AddAsync(PluginLoader.Load(Path.Combine(folder, name + ".dll")));
        _log.Info($"Installed '{name}' {entry.Manifest.Version} from '{entry.Repo.MetaUrl}'.");
        QueueRefresh();
    }

    public async Task UninstallAsync(LoadedPlugin loaded)
    {
        if (loaded.Manifest is null || loaded.Folder is null)
            return;

        await Host.RemoveAsync(Host.Plugins.Where(plugin => plugin.Folder == loaded.Folder).ToList());
        PluginStore.ScheduleDeletion(loaded.Folder);
        _log.Info($"Uninstalled '{loaded.Manifest.InternalName}'; its files go when Aura next starts.");
        QueueRefresh();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        _instance = new Mutex(true, InstanceName, out var first);

        if (!first)
        {
            if (EventWaitHandle.TryOpenExisting(OpenSignalName, out var signal))
            {
                Native.AllowSetForegroundWindow(Native.AllowAnyProcess);
                signal.Set();
                signal.Dispose();
            }

            Shutdown();
            return;
        }

        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            _log.Error("Unhandled error on the UI thread.", args.Exception);
            args.Handled = true;
        };

        Arbiter = new ActivityArbiter();
        Output = new DiscordPresenceOutput();
        var settings = new JsonSettings(AppPaths.SettingsFile);
        Host = new PluginHost(Arbiter, settings);
        Market = new Marketplace(settings);

        _openSignal = new EventWaitHandle(false, EventResetMode.AutoReset, OpenSignalName);
        _openWait = ThreadPool.RegisterWaitForSingleObject(_openSignal, (_, _) => Dispatcher.InvokeAsync(OpenWindow),
            null, Timeout.Infinite, false);

        _tray = new TrayIcon();
        _tray.Activated += OpenWindow;
        _tray.MenuRequested += ShowTrayMenu;

        Arbiter.CurrentChanged += claim =>
        {
            Output.Show(claim);
            QueueRefresh();
        };
        Output.StatusChanged += status =>
        {
            if (!status.Healthy)
                _log.Warn(status.Summary);

            QueueRefresh();
        };
        Host.Changed += QueueRefresh;
        BrowserBridge.Shared.Changed += QueueRefresh;

        _ = StartPluginsAsync();
        _ = Task.Run(() => AppUpdater.RemoveOldFiles(AppContext.BaseDirectory));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _openWait?.Unregister(null);
        _openSignal?.Dispose();
        _tray?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }

    private async Task StartPluginsAsync()
    {
        _log.Info($"Aura started (API {PluginStore.ApiVersion.ToString(2)}).");
        await Host.StartAsync(PluginLoader.LoadAll(AppPaths.DevPlugins, AppPaths.Plugins));

        using var daily = new PeriodicTimer(TimeSpan.FromHours(24));

        do
            await Task.WhenAll(RefreshCatalogAsync(), CheckAppUpdateAsync());
        while (await daily.WaitForNextTickAsync());
    }

    private void QueueRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1)
            return;

        Dispatcher.InvokeAsync(() =>
        {
            _refreshQueued = 0;
            Refresh();
        });
    }

    private void Refresh()
    {
        var claim = Arbiter.Current;
        var problem = !Output.Status.Healthy ||
                      Host.Plugins.Any(plugin => plugin.Enabled && plugin.Context.Health is PluginHealth.Degraded or PluginHealth.Failed);

        _tray?.Update(claim is not null, problem, Tooltip(claim));
        _window?.Refresh();
    }

    private string Tooltip(ActivityClaim? claim)
    {
        var name = AppUpdater.IsDevBuild ? "Aura (dev build)" : "Aura";
        var text = claim is null ? $"{name} · Nothing to show" : $"{name} · {PresenceText.Showing(claim, NameOf(claim.SourceId))}";

        return text.Length <= TooltipLimit ? text : text[..(TooltipLimit - 1)] + "…";
    }

    private void ShowTrayMenu()
    {
        if (_tray is null)
            return;

        var claim = Arbiter.Current;
        var status = new TrayStatus(claim is null ? "Nothing to show" : PresenceText.Showing(claim, NameOf(claim.SourceId)),
            claim is not null, PresenceText.Discord(Output.Status), Output.Status.Healthy);

        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = status, Style = (Style)FindResource("Menu.Status") });
        menu.Items.Add(new Separator());

        if (claim is not null)
        {
            var name = NameOf(claim.SourceId);
            menu.Items.Add(Item(Arbiter.PinnedSourceId == claim.SourceId ? $"Unpin {name}" : $"Pin {name}", () => TogglePin(claim.SourceId)));
        }

        menu.Items.Add(Item("Open Aura", OpenWindow, FontWeights.SemiBold));

        if (AppUpdate is { } release && !AppUpdating)
            menu.Items.Add(Item($"Update Aura to {release.Version}", () => _ = UpdateAuraAsync()));

        menu.Items.Add(Item("Start with Windows", ToggleAutostart, isChecked: Autostart.IsEnabled));
        menu.Items.Add(Item("Open logs folder", () => OpenFolder(AppPaths.Logs)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Exit", () => _ = ExitAsync()));

        _tray.ShowMenu(menu);
    }

    private static MenuItem Item(string header, Action action, FontWeight? weight = null, bool isChecked = false)
    {
        var item = new MenuItem { Header = header, IsChecked = isChecked, FontWeight = weight ?? FontWeights.Normal };
        item.Click += (_, _) => action();

        return item;
    }

    private void ToggleAutostart()
    {
        try
        {
            Autostart.SetEnabled(!Autostart.IsEnabled);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException)
        {
            _log.Error("Changing 'Start with Windows' failed.", exception);
        }

        _log.Info($"Start with Windows: {(Autostart.IsEnabled ? "on" : "off")}.");
    }

    private async Task ExitAsync(bool restart = false)
    {
        _window?.Close();
        _tray?.Dispose();
        _tray = null;
        await Host.DisposeAsync();
        await Output.DisposeAsync();
        _log.Info(restart ? "Aura is restarting." : "Aura stopped.");

        if (restart)
        {
            _instance?.Dispose();
            _instance = null;
            Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, AppUpdater.ExecutableName)) { UseShellExecute = false });
        }

        Shutdown();
    }
}
