using Aura.Presence.Sdk;

namespace Aura.Presence.App.Plugins;

public sealed class MediaBus
{
    private readonly AppLog _log = AppLog.For("MediaBus");
    private readonly Lock _gate = new();
    private Func<MediaUpdate, ValueTask>[] _handlers = [];

    public IMediaBus For(string sourceId) => new SourceBus(this, sourceId);

    private async ValueTask PublishAsync(MediaUpdate update)
    {
        Func<MediaUpdate, ValueTask>[] handlers;

        lock (_gate)
            handlers = _handlers;

        foreach (var handler in handlers)
        {
            try
            {
                await handler(update);
            }
            catch (Exception exception)
            {
                _log.Error($"A media subscriber failed on an update from '{update.SourceId}'.", exception);
            }
        }
    }

    private IDisposable Subscribe(Func<MediaUpdate, ValueTask> handler)
    {
        lock (_gate)
            _handlers = [.. _handlers, handler];

        return new Subscription(() =>
        {
            lock (_gate)
                _handlers = _handlers.Where(existing => existing != handler).ToArray();
        });
    }

    private sealed class SourceBus(MediaBus bus, string sourceId) : IMediaBus
    {
        public ValueTask PublishAsync(NowPlaying? media) =>
            bus.PublishAsync(new MediaUpdate(sourceId, media, DateTimeOffset.UtcNow));

        public IDisposable Subscribe(Func<MediaUpdate, ValueTask> handler) => bus.Subscribe(handler);
    }

    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                unsubscribe();
        }
    }
}
