#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property PublishAot=false
#:project ../src/Aura.Presence.App/Aura.Presence.App.csproj

using System.IO;
using Aura.Presence.App;
using Aura.Presence.App.Arbitration;
using Aura.Presence.App.Plugins;
using Aura.Presence.Sdk;

var settingsFile = Path.Combine(Path.GetTempPath(), $"aura-host-checks-{Guid.NewGuid():N}.json");
var arbiter = new ActivityArbiter();
var host = new PluginHost(arbiter, new JsonSettings(settingsFile));
await host.StartAsync([new Second(), new First()]);

Check(host.Plugins.Select(plugin => plugin.Id).SequenceEqual(["check-first", "check-second"]), "default priority seeds the order");
Check(arbiter.Current?.SourceId == "check-first", "the first plugin in the order shows");

host.SetOrder(["check-second", "check-first"]);
Check(arbiter.Current?.SourceId == "check-second", "a new order changes what shows at once");

var second = host.Plugins.Single(plugin => plugin.Id == "check-second");
var before = (Probe)second.Plugin;
await host.SetEnabledAsync(second, false);
Check(before.Stopped && arbiter.Current?.SourceId == "check-first", "turning a plugin off stops it and hands over");

await host.SetEnabledAsync(second, true);
Check(second.Plugin is Probe { Started: true } fresh && fresh != before, "turning a plugin on starts a fresh instance");
Check(arbiter.Current?.SourceId == "check-second", "the fresh instance takes its place again");

second.Context.Settings.Set("label", "changed");
await host.RestartAsync(second);
Check(second.Plugin is Probe { Label: "changed" }, "a restart reads the changed setting");

second.Context.Settings.Clear();
await host.RestartAsync(second);
Check(second.Plugin is Probe { Label: "default" }, "clearing the settings falls back to defaults");

await host.DisposeAsync();

var reopenedArbiter = new ActivityArbiter();
var reopened = new PluginHost(reopenedArbiter, new JsonSettings(settingsFile));
await reopened.StartAsync([new First(), new Second()]);
Check(reopened.Plugins.Select(plugin => plugin.Id).SequenceEqual(["check-second", "check-first"]), "the order is saved");

var third = new Third();
await reopened.AddAsync([third, new First()]);
Check(reopened.Plugins.Select(plugin => plugin.Id).SequenceEqual(["check-second", "check-first", "check-third"]) && third.Started,
    "an installed plugin starts at once, after the saved order, and a duplicate id is ignored");

var removed = (Probe)reopened.Plugins[0].Plugin;
await reopened.RemoveAsync([reopened.Plugins[0]]);
Check(removed.Stopped && reopened.Plugins.Select(plugin => plugin.Id).SequenceEqual(["check-first", "check-third"]),
    "an uninstalled plugin stops and leaves the list");
Check(reopenedArbiter.Current?.SourceId == "check-first", "the next plugin in line takes over");
await reopened.DisposeAsync();

File.Delete(settingsFile);
Directory.Delete(AppPaths.PluginDataFor("check-first"), recursive: true);
Directory.Delete(AppPaths.PluginDataFor("check-second"), recursive: true);
Directory.Delete(AppPaths.PluginDataFor("check-third"), recursive: true);

Console.WriteLine("All host checks passed.");

static void Check(bool condition, string what)
{
    if (condition)
        return;

    Console.Error.WriteLine($"FAILED: {what}");
    Environment.Exit(1);
}

abstract class Probe : IPresencePlugin
{
    public abstract string Id { get; }

    public string DisplayName => Id;

    public virtual int DefaultPriority => 0;

    public bool Started { get; private set; }

    public bool Stopped { get; private set; }

    public string Label { get; private set; } = "";

    public async Task StartAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        Started = true;
        Label = context.Settings.Get("label", "default");
        await context.Activity.ReportAsync(new PresenceActivity { Details = Label });
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Stopped = true;
        return Task.CompletedTask;
    }
}

sealed class First : Probe
{
    public override string Id => "check-first";

    public override int DefaultPriority => 10;
}

sealed class Second : Probe
{
    public override string Id => "check-second";
}

sealed class Third : Probe
{
    public override string Id => "check-third";
}
