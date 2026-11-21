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
    private static readonly string InstanceName = $@"Local\AuraPresence{AppPaths.InstanceSuffix}.SingleInstance";
    private static readonly string OpenSignalName = $@"Local\AuraPresence{AppPaths.InstanceSuffix}.Open";
    private const int TooltipLimit = 127;
    private const string OneAtATimeSetting = "oneActivityAtATime";
    private const string AutoUpdateSetting = "autoUpdate";

    private readonly AppLog _log = AppLog.For("App");
    private Mutex? _instance;
    private EventWaitHandle? _openSignal;
    private RegisteredWaitHandle? _openWait;
    private TrayIcon? _tray;
    private AuraWindow? _window;
    private JsonSettings _settings = null!;
    private int _refreshQueued;
    private int _catalogRun;
    private readonly HashSet<string> _triedUpdates = [];
    private Native.WinEventProc? _foregroundChanged;

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

    public bool CheckingForUpdates { get; private set; }

    public DateTime? CheckedForUpdatesAt { get; private set; }

    public bool AutoUpdate => _settings.Get(AutoUpdateSetting, true);

    public async Task CheckForUpdatesAsync()
    {
        if (CheckingForUpdates)
            return;

        CheckingForUpdates = true;
        QueueRefresh();

        try
        {
            await Task.WhenAll(RefreshCatalogAsync(), CheckAppUpdateAsync(), Task.Delay(TimeSpan.FromSeconds(1)));
        }
        finally
        {
            CheckingForUpdates = false;
            CheckedForUpdatesAt = DateTime.Now;
            QueueRefresh();
        }

        if (AutoUpdate && _window is null)
            await InstallUpdatesAsync();
    }

    private async Task InstallUpdatesAsync()
    {
        var updates = Host.Plugins.Select(plugin => plugin.Manifest is { } manifest ? Marketplace.AutoUpdateFor(manifest, Catalog) : null)
            .OfType<CatalogEntry>().ToList();

        foreach (var update in updates)
        {
            if (!_triedUpdates.Add($"{update.Manifest.InternalName} {update.Manifest.Version}"))
                continue;

            try
            {
                await InstallAsync(update);
            }
            catch (Exception exception) when (CatalogItem.IsExpected(exception))
            {
                _log.Error($"Updating '{update.Manifest.InternalName}' to {update.Manifest.Version} automatically failed; it waits for a click.", exception);
            }
        }

        if (_window is null && AppUpdate is { } release && _triedUpdates.Add($"Aura {release.Version}"))
            await UpdateAuraAsync();
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

        _settings = new JsonSettings(AppPaths.SettingsFile);
        Arbiter = new ActivityArbiter(DiscordPresenceOutput.SlotOf);
        Arbiter.SetOneAtATime(_settings.Get(OneAtATimeSetting, false));
        Output = new DiscordPresenceOutput();
        Host = new PluginHost(Arbiter, _settings);
        Market = new Marketplace(_settings);

        _openSignal = new EventWaitHandle(false, EventResetMode.AutoReset, OpenSignalName);
        _openWait = ThreadPool.RegisterWaitForSingleObject(_openSignal, (_, _) => Dispatcher.InvokeAsync(OpenWindow),
            null, Timeout.Infinite, false);

        _tray = new TrayIcon();
        _tray.Activated += OpenWindow;
        _tray.MenuRequested += ShowTrayMenu;

        Arbiter.ShownChanged += shown =>
        {
            Output.Show(shown);
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

        if (AppPaths.IsDevBuild)
            OpenWindow();
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
        Dispatcher.Invoke(WatchForeground);

        using var hourly = new PeriodicTimer(TimeSpan.FromHours(1));

        do
            await CheckForUpdatesAsync();
        while (await hourly.WaitForNextTickAsync());
    }

    private void WatchForeground()
    {
        _foregroundChanged = (_, _, window, _, _, _, _) => Dispatcher.InvokeAsync(() => OnForeground(window));
        Native.SetWinEventHook(Native.EventSystemForeground, Native.EventSystemForeground, IntPtr.Zero, _foregroundChanged, 0, 0, 0);
        OnForeground(Native.GetForegroundWindow());
    }

    private void OnForeground(IntPtr window)
    {
        Native.GetWindowThreadProcessId(window, out var processId);
        string name;

        try
        {
            using var process = Process.GetProcessById(processId);
            name = process.ProcessName;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return;
        }

        if (Host.Plugins.FirstOrDefault(plugin => plugin.Manifest?.Processes.Contains(name, StringComparer.OrdinalIgnoreCase) == true) is { } game)
            Arbiter.Foreground(game.Id);
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
        var shown = Arbiter.Shown;
        var problem = !Output.Status.Healthy ||
                      Host.Plugins.Any(plugin => plugin.Enabled && plugin.Context.Health is PluginHealth.Degraded or PluginHealth.Failed);

        _tray?.Update(shown.Count > 0, problem, Tooltip(shown));
        _window?.Refresh();
    }

    private string Showing(IReadOnlyList<ActivityClaim> shown) =>
        shown.Count == 0 ? "Nothing to show" : string.Join("\n", shown.Select(claim => PresenceText.Showing(claim, NameOf(claim.SourceId))));

    private string Tooltip(IReadOnlyList<ActivityClaim> shown)
    {
        var text = $"{(AppUpdater.IsDevBuild ? "Aura (dev build)" : "Aura")} · {Showing(shown)}";

        return text.Length <= TooltipLimit ? text : text[..(TooltipLimit - 1)] + "…";
    }

    private void ShowTrayMenu()
    {
        if (_tray is null)
            return;

        var shown = Arbiter.Shown;
        var status = new TrayStatus(Showing(shown), shown.Count > 0, PresenceText.Discord(Output.Status), Output.Status.Healthy);

        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = status, Style = (Style)FindResource("Menu.Status") });
        menu.Items.Add(new Separator());

        foreach (var claim in shown)
        {
            var name = NameOf(claim.SourceId);
            menu.Items.Add(Item(Arbiter.PinnedSourceId == claim.SourceId ? $"Unpin {name}" : $"Pin {name}", () => TogglePin(claim.SourceId)));
        }

        menu.Items.Add(Item("Open Aura", OpenWindow, FontWeights.SemiBold));

        if (AppUpdate is { } release && !AppUpdating)
            menu.Items.Add(Item($"Update Aura to {release.Version}", () => _ = UpdateAuraAsync()));
        else if (AppUpdate is null)
            menu.Items.Add(Item(CheckingForUpdates ? "Checking for updates…" : "Check for updates", () => _ = CheckForUpdatesAsync()));

        menu.Items.Add(Item("Update automatically", ToggleAutoUpdate, isChecked: AutoUpdate));

        menu.Items.Add(Item("One activity at a time", ToggleOneAtATime, isChecked: Arbiter.OneAtATime));
        if (!AppPaths.IsDevBuild)
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

    private void ToggleOneAtATime()
    {
        Arbiter.SetOneAtATime(!Arbiter.OneAtATime);
        _settings.Set(OneAtATimeSetting, Arbiter.OneAtATime);
        _log.Info($"One activity at a time: {(Arbiter.OneAtATime ? "on" : "off")}.");
        QueueRefresh();
    }

    private void ToggleAutoUpdate()
    {
        _settings.Set(AutoUpdateSetting, !AutoUpdate);
        _log.Info($"Update automatically: {(AutoUpdate ? "on" : "off")}.");
        QueueRefresh();

        if (AutoUpdate)
            _ = CheckForUpdatesAsync();
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
