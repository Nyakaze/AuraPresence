using Aura.Presence.Sdk;

namespace Aura.Presence.Sample;

public sealed class SamplePlugin : IPresencePlugin
{
    public const string ApplicationIdVariable = "AURA_SAMPLE_APP_ID";

    public string Id => "sample";

    public string DisplayName => "Sample";

    public string Description => $"A test plugin to copy when you build your own. It reports one fixed activity while {ApplicationIdVariable} is set.";

    public async Task StartAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var applicationId = Environment.GetEnvironmentVariable(ApplicationIdVariable);

        if (string.IsNullOrWhiteSpace(applicationId))
        {
            context.ReportHealth(PluginHealth.Waiting, $"Inactive; set {ApplicationIdVariable} to enable it.");

            return;
        }

        await context.Activity.ReportAsync(new PresenceActivity
        {
            ApplicationId = applicationId,
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
