using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
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

    public string Byline => $"By Aura · version {Version}";

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

    public void Update(IReadOnlyList<ActivityClaim> ranked, Func<string, string> nameOf)
    {
        IsOn = Loaded.Enabled;
        (Status, Tone) = Describe(ranked, nameOf);
    }

    public static object IconOf(IPresencePlugin plugin)
    {
        var folder = Path.GetDirectoryName(plugin.GetType().Assembly.Location);
        var file = folder is null ? null : Path.Combine(folder, "icon.png");

        if (file is null || !File.Exists(file))
            return Application.Current.FindResource(CatalogItem.All.FirstOrDefault(item => item.Id == plugin.Id)?.IconKey ?? "G.Puzzle");

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(file);
        image.EndInit();
        image.Freeze();

        return image;
    }

    private (string, Tone) Describe(IReadOnlyList<ActivityClaim> ranked, Func<string, string> nameOf)
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

        var winner = ranked.FirstOrDefault();

        if (winner?.SourceId == Id)
            return ($"Showing {PresenceText.Short(winner.Activity)}", Tone.Live);

        if (winner is not null && ranked.FirstOrDefault(claim => claim.SourceId == Id) is { } claim)
            return ($"{PresenceText.Short(claim.Activity)}, behind {nameOf(winner.SourceId)}", Tone.Normal);

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

    public void Update(IReadOnlyList<ActivityClaim> ranked, string? pinnedSourceId, Func<string, string> nameOf, object? icon)
    {
        var winner = ranked[0];
        var activity = winner.Activity;
        var name = nameOf(winner.SourceId);
        var pinned = pinnedSourceId == winner.SourceId;
        var next = ranked.Skip(1).FirstOrDefault();

        SourceId = winner.SourceId;
        Heading = PresenceText.Heading(activity, name);
        Details = activity.Details ?? activity.State ?? "";
        State = activity.Details is null ? null : activity.State;
        Icon = icon;
        Button = activity.Buttons.FirstOrDefault()?.Label;
        PinLabel = pinned ? $"Unpin {name}" : $"Pin {name}";
        Footnote = (pinned ? $"Pinned, so your order is ignored until you unpin {name}." : $"From {name}, first in your order.")
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

public sealed class MainModel : Observable
{
    private NowShowing? _now;
    private int _count;
    private string _discord = "";
    private bool _discordHealthy = true;

    public ObservableCollection<PluginItem> Sources { get; } = [];

    public ObservableCollection<PluginItem> Helpers { get; } = [];

    public NowShowing? Now
    {
        get => _now;
        private set => Set(ref _now, value);
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

    public void Update(App app)
    {
        var plugins = app.Host.Plugins;
        var ranked = app.Arbiter.Ranked();

        Sync(Sources, plugins.Where(plugin => plugin.Plugin.ShowsActivity).ToList());
        Sync(Helpers, plugins.Where(plugin => !plugin.Plugin.ShowsActivity).ToList());

        foreach (var item in Sources.Concat(Helpers))
            item.Update(ranked, app.NameOf);

        for (var i = 0; i < Sources.Count; i++)
            Sources[i].MoveLabel = $"Move {Sources[i].Name}, {i + 1} of {Sources.Count}. Use the arrow keys.";

        Count = plugins.Count;
        Discord = PresenceText.Discord(app.Output.Status);
        DiscordHealthy = app.Output.Status.Healthy;

        if (ranked.Count == 0)
        {
            Now = null;
            return;
        }

        var source = Sources.FirstOrDefault(item => item.Id == ranked[0].SourceId);
        var now = Now ?? new NowShowing();
        now.Update(ranked, app.Arbiter.PinnedSourceId, app.NameOf, source?.Icon);
        Now = now;
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
    private bool _revealed;

    public TextItem(LoadedPlugin loaded, TextSetting setting) : base(loaded, setting)
    {
        _setting = setting;
        _value = Read(setting.Default);
    }

    public bool Monospace => _setting.Monospace;

    public void Reveal(bool revealed)
    {
        if (!_setting.Secret || _revealed == revealed)
            return;

        _revealed = revealed;
        Raise(nameof(Value));
    }

    public string Value
    {
        get => _setting.Secret && _value.Length > 0 && !_revealed ? Mask : _value;
        set
        {
            var trimmed = value.Trim();
            var next = _setting.Secret && trimmed == Mask ? _value
                : trimmed.Length == 0 ? _setting.Default
                : trimmed;

            if (next != _value)
            {
                _value = next;
                Save(next);
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

public sealed class PluginPage
{
    private readonly string _shown;

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

    private static List<PluginSetting> Visible(LoadedPlugin loaded) =>
        [.. loaded.Plugin.Settings.Where(setting => setting.ShowWhen?.Invoke(loaded.Context.Settings) ?? true)];

    private static string Signature(List<PluginSetting> settings) =>
        string.Join('\n', settings.Select(setting => $"{setting.Key}\t{setting.Label}\t{setting.Help}\t{setting.Card}"));
}

public sealed record CatalogItem(string Id, string Name, string Category, string Blurb, string IconKey, IReadOnlyList<string> Uses, bool LimitedAccess)
{
    public object Icon => Application.Current.FindResource(IconKey);

    public bool Installed => App.Current.Host.Plugins.Any(plugin => plugin.Id == Id);

    public string AccessTitle => LimitedAccess ? "Can only set your status" : "Runs inside Aura";

    public string AccessText => LimitedAccess
        ? "Clients reach it over localhost, each with its own token. Requests from websites are refused."
        : "Like any app you install, it can read and change files on this PC. Install plugins you trust.";

    public static IReadOnlyList<CatalogItem> All { get; } =
    [
        new("stremio", "Stremio", "Video", "Shows what you watch in Stremio on your profile. Nothing to set up.", "Logo.Stremio",
            ["Windows media controls, for what is playing", "Stremio’s saved library on disk, for titles and episodes"], false),
        new("media", "Media controls", "Music", "Shows Spotify, VLC, browsers and any app in the Windows media flyout.", "G.AudioLines",
            ["Windows media controls, for the title, artist and position"], false),
        new("anilist-sync", "AniList sync", "Anime", "Marks episodes watched on your AniList as you finish them.", "Logo.AniList",
            ["What your video plugins report as playing", "Your AniList account, after you sign in"], false),
        new("trakt", "Trakt scrobbler", "Video", "Adds the films and episodes you finish to your Trakt history.", "Logo.Trakt",
            ["What your video plugins report as playing", "Your Trakt account, after you sign in"], false),
        new("lastfm", "Last.fm scrobbler", "Music", "Scrobbles the songs you play to your Last.fm profile.", "Logo.LastFm",
            ["What your music plugins report as playing", "Your Last.fm account, after you sign in"], false),
        new("local-api", "Local API", "Developer", "Lets scripts, game mods and browser extensions set your status.", "G.Webhook",
            ["HTTP and WebSocket on this PC only", "One token per client, issued and revoked here"], true)
    ];
}

public sealed record MarketDetailPage(CatalogItem Item);

public sealed class CategoryOption(string label, bool isChecked)
{
    public string Label => label;

    public bool IsChecked { get; set; } = isChecked;
}

public sealed class MarketPage : Observable
{
    private string _query = "";
    private string _category = "All";
    private int _columns = 3;
    private IReadOnlyList<CatalogItem> _results = [];

    public MarketPage() => Filter();

    public IReadOnlyList<CategoryOption> Categories { get; } =
        [.. new[] { "All", "Video", "Music", "Anime", "Developer" }.Select(label => new CategoryOption(label, label == "All"))];

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

    public string? EmptyText => _results.Count > 0 ? null
        : _query.Trim().Length > 0 ? $"No plugin matches “{_query.Trim()}”." : "No plugins in this category yet.";

    private void Filter()
    {
        var query = _query.Trim();
        Results = [.. CatalogItem.All.Where(item =>
            (_category == "All" || item.Category == _category) &&
            (query.Length == 0 || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             item.Blurb.Contains(query, StringComparison.OrdinalIgnoreCase)))];
    }
}
