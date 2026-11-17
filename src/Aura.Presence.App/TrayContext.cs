using Aura.Presence.App.Arbitration;
using Aura.Presence.App.Output;
using Aura.Presence.App.Plugins;
using Aura.Presence.Sdk;

namespace Aura.Presence.App;

public sealed class TrayContext : ApplicationContext
{
    private readonly AppLog _log = AppLog.For("App");
    private readonly SynchronizationContext _ui;
    private readonly ActivityArbiter _arbiter = new();
    private const int TooltipLimit = 127;

    private readonly IPresenceOutput _output = new DiscordPresenceOutput();
    private readonly PluginHost _host;
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _discordItem;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _pluginsItem;
    private readonly ToolStripMenuItem _autostartItem;

    public TrayContext()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _host = new PluginHost(_arbiter, new JsonPluginSettings(Path.Combine(AppPaths.DataRoot, "settings.json")));

        _discordItem = new ToolStripMenuItem(_output.Status.Summary) { Enabled = false };
        _statusItem = new ToolStripMenuItem("Nothing to show") { Enabled = false };
        _pluginsItem = new ToolStripMenuItem("Plugins");
        _autostartItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleAutostart())
        {
            Checked = Autostart.IsEnabled
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_discordItem);
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_pluginsItem);
        menu.Items.Add(_autostartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open logs folder", null, (_, _) => OpenFolder(AppPaths.Logs));
        menu.Items.Add("Exit", null, async (_, _) => await ExitAsync());

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Aura — Rich Presence hub for Discord",
            ContextMenuStrip = menu,
            Visible = true
        };

        _arbiter.CurrentChanged += OnCurrentChanged;
        _output.StatusChanged += status => _ui.Post(_ => ShowOutputStatus(status), null);
        _ = StartAsync();
    }

    private void ShowOutputStatus(OutputStatus status)
    {
        _discordItem.Text = status.Summary;

        var tooltip = $"Aura — {status.Summary}";
        _trayIcon.Text = tooltip.Length <= TooltipLimit ? tooltip : tooltip[..(TooltipLimit - 1)] + "…";

        if (!status.Healthy)
            _log.Warn(status.Summary);
    }

    private async Task StartAsync()
    {
        _log.Info("Aura started.");
        await _host.StartAsync(AppPaths.Plugins);

        foreach (var loaded in _host.Plugins)
        {
            loaded.Context.HealthChanged += (_, _) => _ui.Post(_ => RebuildPluginMenu(), null);
        }

        RebuildPluginMenu();
    }

    private void OnCurrentChanged(ActivityClaim? claim)
    {
        _output.Show(claim);
        _ui.Post(_ => _statusItem.Text = claim is null
            ? "Nothing to show"
            : $"Showing: {claim.Activity.Details ?? claim.SourceId}", null);
    }

    private void RebuildPluginMenu()
    {
        _pluginsItem.DropDownItems.Clear();

        if (_host.Plugins.Count == 0)
        {
            _pluginsItem.DropDownItems.Add(new ToolStripMenuItem("No plugins installed") { Enabled = false });
            return;
        }

        foreach (var loaded in _host.Plugins)
        {
            var message = loaded.Context.HealthMessage;
            var text = loaded.Enabled
                ? $"{loaded.Plugin.DisplayName} — {loaded.Context.Health}{(message is null ? "" : $": {message}")}"
                : $"{loaded.Plugin.DisplayName} — off";

            _pluginsItem.DropDownItems.Add(new ToolStripMenuItem(text, null, async (_, _) => await TogglePluginAsync(loaded))
            {
                Checked = loaded.Enabled
            });
        }
    }

    private async Task TogglePluginAsync(LoadedPlugin loaded)
    {
        await _host.SetEnabledAsync(loaded, !loaded.Enabled);
        RebuildPluginMenu();
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

        _autostartItem.Checked = Autostart.IsEnabled;
        _log.Info($"Start with Windows: {(_autostartItem.Checked ? "on" : "off")}.");
    }

    private static void OpenFolder(string path)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
    }

    private async Task ExitAsync()
    {
        _trayIcon.Visible = false;
        await _host.DisposeAsync();
        await _output.DisposeAsync();
        _log.Info("Aura stopped.");
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _trayIcon.Dispose();
        }

        base.Dispose(disposing);
    }
}
