namespace Aura.Presence.Sdk;

public enum ActivityKind
{
    Playing = 0,
    Listening = 2,
    Watching = 3,
    Competing = 5
}

public sealed record PresenceButton(string Label, Uri Url);

public sealed record PresenceImage(string Key, string? Text = null);

public sealed record PresenceActivity
{
    public ActivityKind Kind { get; init; } = ActivityKind.Playing;

    public string? Name { get; init; }

    public string? Details { get; init; }

    public string? State { get; init; }

    public PresenceImage? LargeImage { get; init; }

    public PresenceImage? SmallImage { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? EndsAt { get; init; }

    public IReadOnlyList<PresenceButton> Buttons { get; init; } = [];
}
