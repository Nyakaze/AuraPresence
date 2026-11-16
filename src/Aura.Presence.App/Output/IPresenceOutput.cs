using Aura.Presence.App.Arbitration;

namespace Aura.Presence.App.Output;

public interface IPresenceOutput : IAsyncDisposable
{
    Task ShowAsync(ActivityClaim? claim, CancellationToken cancellationToken);
}

public sealed class LoggingPresenceOutput : IPresenceOutput
{
    private readonly AppLog _log = AppLog.For("Output");

    public Task ShowAsync(ActivityClaim? claim, CancellationToken cancellationToken)
    {
        _log.Write(Sdk.PluginLogLevel.Information, claim is null
            ? "Presence cleared."
            : $"Presence from '{claim.SourceId}': {claim.Activity.Kind} | {claim.Activity.Details} | {claim.Activity.State}");

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
