using Aura.Presence.Sdk;

namespace Aura.Presence.Stremio;

public sealed class StremioPlugin : IPresencePlugin
{
    public string Id => "stremio";

    public string DisplayName => "Stremio";

    public int DefaultPriority => 100;

    public Task StartAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        context.ReportHealth(PluginHealth.Waiting, "Not implemented yet — port from the Node bridge pending.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
