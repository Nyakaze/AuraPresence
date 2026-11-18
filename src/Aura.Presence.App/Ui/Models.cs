using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Aura.Presence.App.Arbitration;
using Aura.Presence.App.Output;
using Aura.Presence.App.Plugins;
using Aura.Presence.Sdk;

namespace Aura.Presence.App.Ui;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        Raise(name);

        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public enum Tone
{
    Normal,
    Live,
    Warn,
    Fail
}

public sealed record TrayStatus(string Title, bool Live, string Discord, bool DiscordHealthy);

public static class PresenceText
{
    private const string DiscordPrefix = "Discord: ";

    public static string Heading(PresenceActivity activity, string sourceName) => $"{Verb(activity.Kind)} {activity.Name ?? sourceName}";

    public static string Short(PresenceActivity activity) => activity.Details ?? activity.State ?? "an activity";

    public static string Showing(ActivityClaim claim, string sourceName) => $"Showing {sourceName} · {Short(claim.Activity)}";

    public static string Discord(OutputStatus status)
    {
        var text = status.Summary.StartsWith(DiscordPrefix, StringComparison.Ordinal) ? status.Summary[DiscordPrefix.Length..] : status.Summary;

        return text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
    }

    private static string Verb(ActivityKind kind) => kind switch
    {
        ActivityKind.Listening => "Listening to",
        ActivityKind.Watching => "Watching",
        ActivityKind.Competing => "Competing in",
        _ => "Playing"
    };
}

public sealed class ShownConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var shown = value switch
        {
            null => false,
            bool flag => flag,
            string text => text.Length > 0,
            int count => count > 0,
            _ => true
        };

        return shown ^ parameter is "invert" ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class CapsuleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        new CornerRadius(value is double height ? height / 2 : 0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class PluginItem : Observable
{
    private bool _isOn;
    private string _status = "";
    private Tone _tone;
    private string _moveLabel = "";
    private bool _isDragging;
    private CatalogEntry? _update;

    public PluginItem(LoadedPlugin loaded)
    {
        Loaded = loaded;
        Icon = IconOf(loaded.Plugin);
        var version = loaded.Plugin.GetType().Assembly.GetName().Version;
        Version = version is null ? "" : $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
    }

    public LoadedPlugin Loaded { get; }

    public string Id => Loaded.Id;

    public string Name => Loaded.Plugin.DisplayName;

    public string? Description => Loaded.Plugin.Description;

    public string Version { get; }

    public string Byline => Loaded.Manifest?.Author is { } author ? $"By {author} · version {Version}" : $"Dev build · version {Version}";

    public bool CanUninstall => Loaded.Manifest is not null;

    public bool NeedsBrowser => Loaded.Context.UsesBrowser || Loaded.Manifest?.NeedsBrowserExtension == true;

    public CatalogEntry? AvailableUpdate
    {
        get => _update;
        private set
        {
            if (Set(ref _update, value))
                Raise(nameof(UpdateText));
        }
    }

    public string? UpdateText => _update is null ? null : $"Update to {_update.Manifest.ParsedVersion}";

    public object Icon { get; }

    public bool CanMove => Loaded.Plugin.ShowsActivity;

    public bool IsOn
    {
        get => _isOn;
        set => Set(ref _isOn, value);
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public Tone Tone
    {
        get => _tone;
        private set => Set(ref _tone, value);
    }

    public string MoveLabel
    {
        get => _moveLabel;
        set => Set(ref _moveLabel, value);
    }

    public bool IsDragging
    {
        get => _isDragging;
        set => Set(ref _isDragging, value);
    }

    public void Update(IReadOnlyList<ActivityClaim> ranked, Func<ActivityClaim, int> slotOf, Func<string, string> nameOf, CatalogEntry? update)
    {
        IsOn = Loaded.Enabled;
        (Status, Tone) = Describe(ranked, slotOf, nameOf);
        AvailableUpdate = update;
    }

    public static object IconOf(IPresencePlugin plugin)
    {
        var folder = PluginStore.FolderOf(plugin);
        var file = folder is null ? null : Path.Combine(folder, "icon.png");

        return file is not null && File.Exists(file) ? Image(new Uri(file)) : Application.Current.FindResource("G.Puzzle");
    }

    public static ImageSource Image(Uri source)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = source;
        image.EndInit();

        if (source.IsFile)
            image.Freeze();

        return image;
    }

    private (string, Tone) Describe(IReadOnlyList<ActivityClaim> ranked, Func<ActivityClaim, int> slotOf, Func<string, string> nameOf)
    {
        if (!Loaded.Enabled)
            return ("Off", Tone.Normal);

        var message = Loaded.Context.HealthMessage;

        switch (Loaded.Context.Health)
        {
            case PluginHealth.Failed:
                return (message ?? "Stopped with an error.", Tone.Fail);
            case PluginHealth.Degraded:
                return (message ?? "Not working right. Check the log.", Tone.Warn);
        }

        if (!Loaded.Plugin.ShowsActivity)
            return (message ?? "Running.", Tone.Normal);

        if (ranked.FirstOrDefault(claim => claim.SourceId == Id) is { } claim)
        {
            var winner = ranked.First(other => slotOf(other) == slotOf(claim));

            return winner == claim
                ? ($"Showing {PresenceText.Short(claim.Activity)}", Tone.Live)
                : ($"{PresenceText.Short(claim.Activity)}, behind {nameOf(winner.SourceId)}", Tone.Normal);
        }

        return (message ?? "Waiting for activity.", Tone.Normal);
    }
}

public sealed class NowShowing : Observable
{
    private string _sourceId = "";
    private string _heading = "";
    private string _details = "";
    private string? _state;
    private object? _icon;
    private ImageSource? _poster;
    private string? _posterUrl;
    private string? _button;
    private string? _elapsed;
    private string _footnote = "";
    private string _pinLabel = "";
    private DateTimeOffset? _startedAt;

    public string SourceId
    {
        get => _sourceId;
        private set => Set(ref _sourceId, value);
    }

    public string Heading
    {
        get => _heading;
        private set => Set(ref _heading, value);
    }

    public string Details
    {
        get => _details;
        private set => Set(ref _details, value);
    }

    public string? State
    {
        get => _state;
        private set => Set(ref _state, value);
    }

    public object? Icon
    {
        get => _icon;
        private set => Set(ref _icon, value);
    }

    public ImageSource? Poster
    {
        get => _poster;
        private set => Set(ref _poster, value);
    }

    public string? Button
    {
        get => _button;
        private set => Set(ref _button, value);
    }

    public string? Elapsed
    {
        get => _elapsed;
        private set => Set(ref _elapsed, value);
    }

    public string Footnote
    {
        get => _footnote;
        private set => Set(ref _footnote, value);
    }

    public string PinLabel
    {
        get => _pinLabel;
        private set => Set(ref _pinLabel, value);
    }

    public void Update(ActivityClaim winner, ActivityClaim? next, string? pinnedSourceId, Func<string, string> nameOf, object? icon)
    {
        var activity = winner.Activity;
        var name = nameOf(winner.SourceId);
        var pinned = pinnedSourceId == winner.SourceId;

        SourceId = winner.SourceId;
        Heading = PresenceText.Heading(activity, name);
        Details = activity.Details ?? activity.State ?? "";
        State = activity.Details is null ? null : activity.State;
        Icon = icon;
        Button = activity.Buttons.FirstOrDefault()?.Label;
        PinLabel = pinned ? $"Unpin {name}" : $"Pin {name}";
        Footnote = (pinned ? $"Pinned, so your order is ignored until you unpin {name}." : $"From {name}.")
                   + (next is null ? "" : $" Next in line: {PresenceText.Short(next.Activity)}, from {nameOf(next.SourceId)}.");
        _startedAt = activity.StartedAt;
        ShowPoster(activity.LargeImage?.Key);
        Tick();
    }

    public void Tick()
    {
        if (_startedAt is not { } started)
        {
            Elapsed = null;
            return;
        }

        var span = DateTimeOffset.UtcNow - started;
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;

        Elapsed = (span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss")) + " elapsed";
    }

    private void ShowPoster(string? key)
    {
        var url = Uri.TryCreate(key, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri : null;

        if (url?.AbsoluteUri == _posterUrl)
            return;

        _posterUrl = url?.AbsoluteUri;
        Poster = url is null ? null : new BitmapImage(url);
    }
}

public sealed class BrowserSetup : Observable
{
    public static BrowserSetup Shared { get; } = new();

    public bool Connected => BrowserBridge.Shared.IsConnected;

    public bool Known => Connected || BrowserBridge.Shared.KnownBrowsers.Count > 0;

    public string Status => BrowserBridge.Shared switch
    {
        { Browsers: { Count: > 0 } connected } => $"Connected to {string.Join(" and ", connected)}.",
        { KnownBrowsers: { Count: > 0 } known } =>
            $"Added to {string.Join(" and ", known)}, which {(known.Count == 1 ? "isn’t" : "aren’t")} running right now.",
        _ => "Not added to a browser yet."
    };

    public string CardText => Known ? "Aura browser extension added" : "Needs the Aura browser extension";

    public void Refresh() => Raise(string.Empty);
}

public sealed class MainModel : Observable
{
    private int _count;
    private string _discord = "";
    private bool _discordHealthy = true;
    private string? _updateText;
    private string? _updateTip;
    private bool _canUpdate;
    private bool _showsBrowsers;
    private bool _browserConnected;
    private string _browserList = "";
    private string _orderHint = "";

    public ObservableCollection<PluginItem> Sources { get; } = [];

    public ObservableCollection<PluginItem> Helpers { get; } = [];

    public ObservableCollection<NowShowing> Now { get; } = [];

    public string OrderHint
    {
        get => _orderHint;
        private set => Set(ref _orderHint, value);
    }

    public int Count
    {
        get => _count;
        private set => Set(ref _count, value);
    }

    public string Discord
    {
        get => _discord;
        private set => Set(ref _discord, value);
    }

    public bool DiscordHealthy
    {
        get => _discordHealthy;
        private set => Set(ref _discordHealthy, value);
    }

    public string AppVersion { get; } = $"v{AppUpdater.Current}";

    public bool IsDevBuild => AppUpdater.IsDevBuild;

    public string AppLocation { get; } = (AppUpdater.IsDevBuild
        ? "Dev build: it doesn’t update itself. Your installed Aura is a different copy. "
        : "") + $"Running from {AppUpdater.InstallDirectory}. Click to open the folder.";

    public string? UpdateText
    {
        get => _updateText;
        private set => Set(ref _updateText, value);
    }

    public string? UpdateTip
    {
        get => _updateTip;
        private set => Set(ref _updateTip, value);
    }

    public bool CanUpdate
    {
        get => _canUpdate;
        private set => Set(ref _canUpdate, value);
    }

    public bool ShowsBrowsers
    {
        get => _showsBrowsers;
        private set => Set(ref _showsBrowsers, value);
    }

    public bool BrowserConnected
    {
        get => _browserConnected;
        private set => Set(ref _browserConnected, value);
    }

    public string BrowserList
    {
        get => _browserList;
        private set => Set(ref _browserList, value);
    }

    public void Update(App app)
    {
        var plugins = app.Host.Plugins;
        var ranked = app.Arbiter.Ranked();

        Sync(Sources, plugins.Where(plugin => plugin.Plugin.ShowsActivity).ToList());
        Sync(Helpers, plugins.Where(plugin => !plugin.Plugin.ShowsActivity).ToList());

        foreach (var item in Sources.Concat(Helpers))
            item.Update(ranked, app.Arbiter.SlotOf, app.NameOf, app.UpdateFor(item.Loaded));

        for (var i = 0; i < Sources.Count; i++)
            Sources[i].MoveLabel = $"Move {Sources[i].Name}, {i + 1} of {Sources.Count}. Use the arrow keys.";

        Count = plugins.Count;
        OrderHint = app.Arbiter.OneAtATime
            ? "When two plugins have something to show, the higher one wins. Drag to change the order."
            : "Watching and Playing show side by side. When two plugins show the same kind, the higher one wins. Drag to change the order.";
        Discord = PresenceText.Discord(app.Output.Status);
        DiscordHealthy = app.Output.Status.Healthy;
        UpdateText = app.AppUpdate is not { } release ? null
            : app.AppUpdating ? "Updating…"
            : app.AppUpdateError is not null ? "Update failed · retry"
            : $"Update to {release.Version}";
        UpdateTip = app.AppUpdate is null ? null
            : app.AppUpdateError ?? $"Aura {app.AppUpdate.Version} is available. Aura downloads it and restarts.";
        CanUpdate = app.AppUpdate is not null && !app.AppUpdating;

        var connected = BrowserBridge.Shared.Browsers;
        var known = BrowserBridge.Shared.KnownBrowsers.Union(connected).ToList();
        BrowserConnected = connected.Count > 0;
        BrowserList = known.Count == 0
            ? "Not added to a browser yet."
            : string.Join("\n", known.Select(name => $"{name} · {(connected.Contains(name) ? "connected" : "not running")}"));
        ShowsBrowsers = known.Count > 0 || Sources.Concat(Helpers).Any(item => item.NeedsBrowser);

        var shown = ranked.DistinctBy(app.Arbiter.SlotOf).ToList();

        while (Now.Count > shown.Count)
            Now.RemoveAt(Now.Count - 1);

        for (var i = 0; i < shown.Count; i++)
        {
            if (i == Now.Count)
                Now.Add(new NowShowing());

            var winner = shown[i];
            var next = ranked.FirstOrDefault(claim => claim != winner && app.Arbiter.SlotOf(claim) == app.Arbiter.SlotOf(winner));
            Now[i].Update(winner, next, app.Arbiter.PinnedSourceId, app.NameOf, Sources.FirstOrDefault(item => item.Id == winner.SourceId)?.Icon);
        }
    }

    private static void Sync(ObservableCollection<PluginItem> items, List<LoadedPlugin> wanted)
    {
        for (var i = 0; i < wanted.Count; i++)
        {
            var index = IndexOf(items, wanted[i]);

            if (index < 0)
                items.Insert(i, new PluginItem(wanted[i]));
            else if (index != i)
                items.Move(index, i);
        }

        while (items.Count > wanted.Count)
            items.RemoveAt(items.Count - 1);
    }

    private static int IndexOf(ObservableCollection<PluginItem> items, LoadedPlugin loaded)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Loaded == loaded)
                return i;
        }

        return -1;
    }
}

public abstract class SettingItem(LoadedPlugin loaded, PluginSetting setting) : Observable
{
    public string Label => setting.Label;

    public string? Help => setting.Help;

    protected IPluginSettings Settings => loaded.Context.Settings;

    protected T Read<T>(T fallback) => Settings.Get(setting.Key, fallback);

    protected void Save<T>(T value) => _ = App.Current.ApplySettingAsync(loaded, setting.Key, value);

    public static SettingItem? For(LoadedPlugin loaded, PluginSetting setting) => setting switch
    {
        TextSetting text => new TextItem(loaded, text),
        ToggleSetting toggle => new ToggleItem(loaded, toggle),
        NumberSetting number => new NumberItem(loaded, number),
        ChoiceSetting choice => new ChoiceItem(loaded, choice),
        LinkSetting link => new LinkItem(loaded, link),
        _ => null
    };
}

public sealed class TextItem : SettingItem
{
    public const string Mask = "••••••••••••";

    private readonly TextSetting _setting;
    private string _value;
    private string _text;
    private bool _revealed;

    public TextItem(LoadedPlugin loaded, TextSetting setting) : base(loaded, setting)
    {
        _setting = setting;
        _value = Read(setting.Default);
        _text = _value == setting.Default ? "" : _value;
    }

    public bool Monospace => _setting.Monospace;

    public string Placeholder => _setting.Default;

    public void Reveal(bool revealed)
    {
        if (!_setting.Secret || _revealed == revealed)
            return;

        _revealed = revealed;
        Raise(nameof(Value));
    }

    public string Value
    {
        get => _setting.Secret && _text.Length > 0 && !_revealed ? Mask : _text;
        set
        {
            var trimmed = value.Trim();

            if (!(_setting.Secret && trimmed == Mask))
            {
                _text = trimmed;
                var next = trimmed.Length == 0 ? _setting.Default : trimmed;

                if (next != _value)
                {
                    _value = next;
                    Save(next);
                }
            }

            Raise();
        }
    }
}

public sealed class LinkItem(LoadedPlugin loaded, LinkSetting setting) : SettingItem(loaded, setting)
{
    public string ButtonText => setting.ButtonText;

    public Uri Url => setting.Url(Settings);
}

public sealed class ToggleItem : SettingItem
{
    private bool _value;

    public ToggleItem(LoadedPlugin loaded, ToggleSetting setting) : base(loaded, setting) => _value = Read(setting.Default);

    public bool Value
    {
        get => _value;
        set
        {
            if (Set(ref _value, value))
                Save(value);
        }
    }
}

public sealed class NumberItem : SettingItem
{
    private readonly NumberSetting _setting;
    private int _value;

    public NumberItem(LoadedPlugin loaded, NumberSetting setting) : base(loaded, setting)
    {
        _setting = setting;
        _value = Read(setting.Default);
    }

    public string? Unit => _setting.Unit;

    public string Value
    {
        get => _value.ToString(CultureInfo.CurrentCulture);
        set
        {
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out var number))
            {
                number = Math.Clamp(number, _setting.Minimum, _setting.Maximum);

                if (number != _value)
                {
                    _value = number;
                    Save(number);
                }
            }

            Raise();
        }
    }
}

public sealed class ChoiceItem : SettingItem
{
    public ChoiceItem(LoadedPlugin loaded, ChoiceSetting setting) : base(loaded, setting)
    {
        var current = Read(setting.Default);
        Options = [.. setting.Options.Select(option => new ChoiceOption(this, option, option == current))];
    }

    public string GroupName { get; } = Guid.NewGuid().ToString("N");

    public IReadOnlyList<ChoiceOption> Options { get; }

    internal void Select(string option) => Save(option);
}

public sealed class ChoiceOption(ChoiceItem owner, string label, bool isChecked) : Observable
{
    private bool _isChecked = isChecked;

    public string Label => label;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (Set(ref _isChecked, value) && value)
                owner.Select(label);
        }
    }
}

public sealed class PluginPage : Observable
{
    private readonly string _shown;
    private bool _confirming;
    private bool _busy;
    private string? _error;

    public PluginPage(PluginItem plugin)
    {
        Plugin = plugin;
        var visible = Visible(plugin.Loaded);
        _shown = Signature(visible);
        var cards = new List<List<SettingItem>>();

        for (var i = 0; i < visible.Count; i++)
        {
            if (SettingItem.For(plugin.Loaded, visible[i]) is not { } item)
                continue;

            if (cards.Count == 0 || visible[i].Card != visible[i - 1].Card)
                cards.Add([]);

            cards[^1].Add(item);
        }

        Cards = cards;
    }

    public PluginItem Plugin { get; }

    public bool IsCurrent => _shown == Signature(Visible(Plugin.Loaded));

    public IReadOnlyList<IReadOnlyList<SettingItem>> Cards { get; }

    public bool HasSettings => Cards.Count > 0;

    public bool Confirming
    {
        get => _confirming;
        set => Set(ref _confirming, value);
    }

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (Set(ref _busy, value))
                Raise(nameof(Idle));
        }
    }

    public bool Idle => !_busy;

    public string? Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }

    public string UninstallQuestion => $"Uninstall {Plugin.Name}?";

    public bool NeedsBrowser => Plugin.NeedsBrowser;

    public void Refresh() => Raise(nameof(NeedsBrowser));

    public async Task<bool> RunAsync(Func<Task> action)
    {
        Busy = true;
        Error = null;

        try
        {
            await action();
            return true;
        }
        catch (Exception exception) when (CatalogItem.IsExpected(exception))
        {
            Error = Marketplace.Describe(exception);
            AppLog.For("Ui").Error($"'{Plugin.Id}' failed.", exception);
            return false;
        }
        finally
        {
            Busy = false;
        }
    }

    private static List<PluginSetting> Visible(LoadedPlugin loaded) =>
        [.. loaded.Plugin.Settings.Where(setting => setting.ShowWhen?.Invoke(loaded.Context.Settings) ?? true)];

    private static string Signature(List<PluginSetting> settings) =>
        string.Join('\n', settings.Select(setting => $"{setting.Key}\t{setting.Label}\t{setting.Help}\t{setting.Card}"));
}

public sealed class CatalogItem : Observable
{
    private bool _busy;
    private string? _error;

    public CatalogItem(CatalogEntry entry)
    {
        Entry = entry;
        Icon = Uri.TryCreate(entry.Manifest.IconUrl, UriKind.Absolute, out var icon)
            ? PluginItem.Image(icon)
            : Application.Current.FindResource("G.Puzzle");
    }

    public CatalogEntry Entry { get; }

    private PluginManifest Manifest => Entry.Manifest;

    public string Name => Manifest.Name;

    public string Byline => $"By {Manifest.Author ?? Entry.Repo.Name}";

    public string Category => string.IsNullOrWhiteSpace(Manifest.Category) ? "Other" : Manifest.Category;

    public string Blurb => Manifest.Description ?? "";

    public IReadOnlyList<string> Uses => Manifest.Uses;

    public bool NeedsBrowserExtension => Manifest.NeedsBrowserExtension;

    public object Icon { get; }

    public bool ThirdParty => !Entry.Repo.Official;

    public string Details =>
        $"Version {Manifest.ParsedVersion}" + (Manifest.Size > 0 ? $" · {Manifest.Size / 1048576.0:0.0} MB" : "") +
        (ThirdParty ? $" · from {Entry.Repo.Name}" : "");

    public string AccessTitle => ThirdParty ? "From a third-party repo" : "Runs inside Aura";

    public string AccessText => ThirdParty
        ? $"It comes from {Entry.Repo.Name}, which Aura doesn’t check. Like any app you install, it can read and change files on this PC and use your accounts. Install it only if you trust whoever runs that repo."
        : "Like any app you install, it can read and change files on this PC. Install plugins you trust.";

    public LoadedPlugin? Installed => App.Current.Host.Plugins.FirstOrDefault(plugin =>
        string.Equals(plugin.Manifest?.InternalName, Manifest.InternalName, StringComparison.OrdinalIgnoreCase));

    public bool IsInstalled => Installed is not null;

    public bool HasUpdate => Installed is { } installed && App.Current.UpdateFor(installed) == Entry;

    public bool Compatible => PluginStore.IsCompatible(Manifest.SdkVersion);

    public bool CanInstall => !_busy && Compatible && (!IsInstalled || HasUpdate);

    public bool ShowsButton => !IsInstalled || HasUpdate || _busy;

    public string State => IsInstalled ? HasUpdate ? "Update available" : "Installed"
        : Compatible ? $"Version {Manifest.ParsedVersion}" : "Needs a newer Aura";

    public string ActionText => _busy ? IsInstalled ? "Updating…" : "Installing…"
        : !Compatible ? "Needs a newer Aura"
        : HasUpdate ? $"Update to {Manifest.ParsedVersion}" : "Install";

    public string? Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }

    public static bool IsExpected(Exception exception) =>
        exception is HttpRequestException or TaskCanceledException or InvalidDataException or IOException
            or UnauthorizedAccessException or BadImageFormatException;

    public async Task InstallAsync()
    {
        if (!CanInstall)
            return;

        _busy = true;
        Error = null;
        Refresh();

        try
        {
            await App.Current.InstallAsync(Entry);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            Error = Marketplace.Describe(exception);
            AppLog.For("Ui").Error($"Installing '{Manifest.InternalName}' failed.", exception);
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }

    public void Refresh()
    {
        foreach (var name in new[] { nameof(IsInstalled), nameof(HasUpdate), nameof(CanInstall), nameof(ShowsButton), nameof(State), nameof(ActionText) })
            Raise(name);
    }
}

public sealed record MarketDetailPage(CatalogItem Item);

public sealed class CategoryOption(string label, bool isChecked)
{
    public string Label => label;

    public bool IsChecked { get; set; } = isChecked;
}

public sealed class MarketPage : Observable
{
    private const string All = "All";

    private string _query = "";
    private string _category = All;
    private int _columns = 3;
    private IReadOnlyList<CatalogEntry>? _catalog;
    private IReadOnlyList<CatalogItem> _items = [];
    private IReadOnlyList<CatalogItem> _results = [];
    private IReadOnlyList<CategoryOption> _categories = [new(All, true)];
    private string? _notice;
    private string _reposLabel = "Repos";
    private bool _loading;

    public IReadOnlyList<CategoryOption> Categories
    {
        get => _categories;
        private set => Set(ref _categories, value);
    }

    public string Query
    {
        get => _query;
        set
        {
            if (Set(ref _query, value))
                Filter();
        }
    }

    public string Category
    {
        get => _category;
        set
        {
            if (Set(ref _category, value))
                Filter();
        }
    }

    public int Columns
    {
        get => _columns;
        set => Set(ref _columns, value);
    }

    public IReadOnlyList<CatalogItem> Results
    {
        get => _results;
        private set
        {
            Set(ref _results, value);
            Raise(nameof(EmptyText));
        }
    }

    public string? Notice
    {
        get => _notice;
        private set => Set(ref _notice, value);
    }

    public string ReposLabel
    {
        get => _reposLabel;
        private set => Set(ref _reposLabel, value);
    }

    public bool Loading
    {
        get => _loading;
        private set
        {
            if (Set(ref _loading, value))
                Raise(nameof(EmptyText));
        }
    }

    public string? EmptyText => _results.Count > 0 || (_loading && _items.Count == 0) ? null
        : _query.Trim().Length > 0 ? $"No plugin matches “{_query.Trim()}”."
        : _items.Count == 0 ? "No plugins to show yet." : "No plugins in this category.";

    public void Update(App app)
    {
        Loading = app.CatalogLoading;
        Notice = NoticeFor(app.Repos, app.CatalogLoading);
        ReposLabel = app.Market.CustomRepos.Count == 0 ? "Repos" : $"Repos · {app.Market.CustomRepos.Count + 1}";

        if (ReferenceEquals(_catalog, app.Catalog))
        {
            foreach (var item in _items)
                item.Refresh();

            return;
        }

        _catalog = app.Catalog;
        _items = [.. app.Catalog
            .GroupBy(entry => entry.Manifest.InternalName, StringComparer.OrdinalIgnoreCase)
            .Select(group => new CatalogItem(Pick(app, [.. group])))
            .OrderBy(item => item.ThirdParty).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)];

        string[] labels = [All, .. _items.Select(item => item.Category).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase)];

        if (!labels.Contains(_category))
            _category = All;

        Categories = [.. labels.Select(label => new CategoryOption(label, label == _category))];
        Filter();
    }

    private static CatalogEntry Pick(App app, IReadOnlyList<CatalogEntry> entries)
    {
        var installed = app.Host.Plugins.FirstOrDefault(plugin =>
            string.Equals(plugin.Manifest?.InternalName, entries[0].Manifest.InternalName, StringComparison.OrdinalIgnoreCase))?.Manifest;

        return installed is null ? entries[0] : Marketplace.Source(installed, entries) ?? entries[0];
    }

    private static string? NoticeFor(IReadOnlyList<RepoResult> repos, bool loading)
    {
        if (repos.Count == 0)
            return loading ? "Loading the plugin list…" : null;

        if (repos.FirstOrDefault(repo => repo.Official) is { Error: { } error })
            return $"Can’t load the official plugin list. {error} Installed plugins keep working." +
                   (repos.Count > 1 ? " Custom repos wait until it loads." : "");

        var failed = repos.Where(repo => repo.Error is not null).Select(repo => $"{repo.Name}: {repo.Error}").ToList();

        return failed.Count == 0 ? null : string.Join("\n", failed);
    }

    private void Filter()
    {
        var query = _query.Trim();
        Results = [.. _items.Where(item =>
            (_category == All || string.Equals(item.Category, _category, StringComparison.OrdinalIgnoreCase)) &&
            (query.Length == 0 || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             item.Blurb.Contains(query, StringComparison.OrdinalIgnoreCase)))];
    }
}

public sealed record RepoRow(string Name, string MetaUrl, string Status, bool Failed, bool Official);

public sealed class ReposPage : Observable
{
    private string _url = "";
    private string? _error;
    private bool _busy;
    private IReadOnlyList<RepoRow> _custom = [];
    private RepoRow? _official;

    public bool WarningAccepted => App.Current.Market.WarningAccepted;

    public RepoRow? Official
    {
        get => _official;
        private set => Set(ref _official, value);
    }

    public IReadOnlyList<RepoRow> Custom
    {
        get => _custom;
        private set => Set(ref _custom, value);
    }

    public string Url
    {
        get => _url;
        set => Set(ref _url, value);
    }

    public string? Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (Set(ref _busy, value))
                Raise(nameof(Idle));
        }
    }

    public bool Idle => !_busy;

    public void Accept()
    {
        App.Current.Market.WarningAccepted = true;
        Raise(nameof(WarningAccepted));
    }

    public void Update(App app)
    {
        Official = Row(app.Repos.FirstOrDefault(repo => repo.Official), Marketplace.OfficialUrl, true);
        Custom = [.. app.Market.CustomRepos.Select(url =>
            Row(app.Repos.FirstOrDefault(repo => !repo.Official && repo.MetaUrl == url), url, false))];
    }

    public async Task AddAsync()
    {
        if (Busy || Url.Trim().Length == 0)
            return;

        Busy = true;
        Error = null;

        try
        {
            var result = await App.Current.Market.AddRepoAsync(Url);

            if (result.Meta is null)
            {
                Error = result.Error;
                return;
            }

            Url = "";
            await App.Current.RefreshCatalogAsync();
        }
        finally
        {
            Busy = false;
        }
    }

    public async Task RemoveAsync(RepoRow row)
    {
        App.Current.Market.RemoveRepo(row.MetaUrl);
        await App.Current.RefreshCatalogAsync();
    }

    private static RepoRow Row(RepoResult? result, string url, bool official) => result switch
    {
        { Error: { } error } => new RepoRow(result.Name, url, error, true, official),
        { Meta: { } meta } => new RepoRow(result.Name, url, meta.Plugins.Count == 1 ? "1 plugin" : $"{meta.Plugins.Count} plugins", false, official),
        _ => new RepoRow(url, url, "Not loaded yet.", false, official)
    };
}
