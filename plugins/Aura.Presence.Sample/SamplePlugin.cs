using Aura.Presence.Sdk;

namespace Aura.Presence.Sample;

public sealed class SamplePlugin : IPresencePlugin
{
    public const string EnableVariable = "AURA_SAMPLE";

    public string Id => "sample";

    public string DisplayName => "Sample";

    public string Description => $"A test plugin to copy when you build your own. It reports one fixed activity while {EnableVariable} is set.";

    public async Task StartAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnableVariable)))
        {
            context.ReportHealth(PluginHealth.Waiting, $"Inactive; set {EnableVariable} to enable it.");

            return;
        }

        await context.Activity.ReportAsync(new PresenceActivity
        {
            Kind = ActivityKind.Playing,
            Details = "Testing Aura",
            State = "Sample plugin",
            StartedAt = DateTimeOffset.UtcNow,
            Buttons = [new PresenceButton("Aura", new Uri("https://github.com"))]
        });

        context.ReportHealth(PluginHealth.Healthy, "Reporting a test activity.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
