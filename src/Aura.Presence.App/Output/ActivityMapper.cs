using Aura.Presence.Sdk;
using Crovus.Models;
using Crovus.RichPresence;
using PresenceActivity = Aura.Presence.Sdk.PresenceActivity;

namespace Aura.Presence.App.Output;

public static class ActivityMapper
{
    private const char Ellipsis = '…';
    private const char InvisiblePadding = '⠀';

    public static RichPresenceActivity ToRichPresence(PresenceActivity activity)
    {
        var startedAt = activity.StartedAt;
        var endsAt = activity.EndsAt is { } end && (startedAt is null || end > startedAt) ? activity.EndsAt : null;

        return new RichPresenceActivity
        {
            Type = activity.Kind switch
            {
                ActivityKind.Listening => ActivityType.Listening,
                ActivityKind.Watching => ActivityType.Watching,
                ActivityKind.Competing => ActivityType.Competing,
                _ => ActivityType.Playing
            },
            Name = Text(activity.Name),
            Details = Text(activity.Details),
            State = Text(activity.State),
            StartedAt = startedAt,
            EndsAt = endsAt,
            Assets = Assets(activity.LargeImage, activity.SmallImage),
            Buttons = activity.Buttons
                .Where(button => IsUsableUrl(button.Url))
                .Select(button => new RichPresenceButton(Truncate(button.Label.Trim(), RichPresenceLimits.ButtonLabel),
                    button.Url.AbsoluteUri))
                .Where(button => button.Label.Length > 0)
                .Take(RichPresenceLimits.Buttons)
                .ToArray()
        };
    }

    private static RichPresenceAssets? Assets(PresenceImage? large, PresenceImage? small)
    {
        var largeKey = ImageKey(large);
        var smallKey = ImageKey(small);

        if (largeKey is null && smallKey is null)
            return null;

        return new RichPresenceAssets(largeKey, largeKey is null ? null : Text(large?.Text), smallKey,
            smallKey is null ? null : Text(small?.Text));
    }

    private static string? ImageKey(PresenceImage? image) =>
        image?.Key is { Length: > 0 and <= RichPresenceLimits.AssetKey } key ? key : null;

    private static string? Text(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var text = Truncate(value.Trim(), RichPresenceLimits.TextMax);

        return text.Length < RichPresenceLimits.TextMin ? text.PadRight(RichPresenceLimits.TextMin, InvisiblePadding) : text;
    }

    private static string Truncate(string value, int maximum) =>
        value.Length <= maximum ? value : value[..(maximum - 1)].TrimEnd() + Ellipsis;

    private static bool IsUsableUrl(Uri url) =>
        url.IsAbsoluteUri && url.Scheme is "http" or "https" && url.AbsoluteUri.Length <= RichPresenceLimits.Url;
}
