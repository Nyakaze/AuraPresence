namespace Aura.Presence.Sdk;

public abstract record PluginSetting(string Key, string Label, string? Help)
{
    public Func<IPluginSettings, bool>? ShowWhen { get; init; }

    public string? Card { get; init; }
}

public sealed record TextSetting(string Key, string Label, string Default, string? Help = null, bool Monospace = false, bool Secret = false)
    : PluginSetting(Key, Label, Help);

public sealed record ToggleSetting(string Key, string Label, bool Default, string? Help = null)
    : PluginSetting(Key, Label, Help);

public sealed record NumberSetting(string Key, string Label, int Default, int Minimum, int Maximum, string? Unit = null, string? Help = null)
    : PluginSetting(Key, Label, Help);

public sealed record ChoiceSetting(string Key, string Label, IReadOnlyList<string> Options, string Default, string? Help = null)
    : PluginSetting(Key, Label, Help);

public sealed record LinkSetting(string Label, Func<IPluginSettings, Uri> Url, string ButtonText, string? Help = null)
    : PluginSetting(Label, Label, Help);
