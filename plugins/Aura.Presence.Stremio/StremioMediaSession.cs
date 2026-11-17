using Aura.Presence.Sdk;
using Windows.Media.Control;

namespace Aura.Presence.Stremio;

public sealed record StremioPlayback(string FileName, PlaybackState State, TimeSpan? Position, TimeSpan? Duration);

public sealed class StremioMediaSession
{
    private static readonly TimeSpan PlaceholderDuration = TimeSpan.FromMinutes(1);

    private GlobalSystemMediaTransportControlsSessionManager? _manager;

    public async Task<StremioPlayback?> ReadAsync()
    {
        _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

        var session = _manager.GetSessions()
            .FirstOrDefault(candidate => candidate.SourceAppUserModelId.Contains("stremio", StringComparison.OrdinalIgnoreCase));

        if (session is null)
            return null;

        var properties = await session.TryGetMediaPropertiesAsync();

        if (string.IsNullOrWhiteSpace(properties.Title))
            return null;

        var timeline = session.GetTimelineProperties();
        var duration = timeline.EndTime > PlaceholderDuration ? timeline.EndTime : (TimeSpan?)null;

        var state = duration is null
            ? PlaybackState.Loading
            : session.GetPlaybackInfo().PlaybackStatus switch
            {
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => PlaybackState.Playing,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => PlaybackState.Paused,
                _ => PlaybackState.Loading
            };

        var position = timeline.Position;

        if (state is PlaybackState.Playing && timeline.LastUpdatedTime > DateTimeOffset.MinValue)
            position += DateTimeOffset.Now - timeline.LastUpdatedTime;

        if (duration is { } end && position > end)
            position = end;

        return new StremioPlayback(properties.Title, state, position, duration);
    }
}
