#:property TargetFramework=net10.0-windows10.0.19041.0
#:property UseWPF=true
#:property PublishAot=false
#:project ../src/Aura.Presence.App/Aura.Presence.App.csproj

using System.IO;
using Aura.Presence.App;
using Aura.Presence.App.Arbitration;
using Aura.Presence.App.Output;
using Aura.Presence.App.Plugins;
using Aura.Presence.Sdk;

var settingsFile = Path.Combine(Path.GetTempPath(), $"aura-host-checks-{Guid.NewGuid():N}.json");
var arbiter = new ActivityArbiter();
var host = new PluginHost(arbiter, new JsonSettings(settingsFile));
await host.StartAsync([new Second(), new First()]);

Check(host.Plugins.Select(plugin => plugin.Id).SequenceEqual(["check-first", "check-second"]), "default priority seeds the order");
Check(arbiter.Shown.FirstOrDefault()?.SourceId == "check-first", "the first plugin in the order shows");

host.SetOrder(["check-second", "check-first"]);
Check(arbiter.Shown.FirstOrDefault()?.SourceId == "check-second", "a new order changes what shows at once");

var second = host.Plugins.Single(plugin => plugin.Id == "check-second");
var before = (Probe)second.Plugin;
await host.SetEnabledAsync(second, false);
Check(before.Stopped && arbiter.Shown.FirstOrDefault()?.SourceId == "check-first", "turning a plugin off stops it and hands over");

await host.SetEnabledAsync(second, true);
Check(second.Plugin is Probe { Started: true } fresh && fresh != before, "turning a plugin on starts a fresh instance");
Check(arbiter.Shown.FirstOrDefault()?.SourceId == "check-second", "the fresh instance takes its place again");

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
Check(reopenedArbiter.Shown.FirstOrDefault()?.SourceId == "check-first", "the next plugin in line takes over");
await reopened.DisposeAsync();

File.Delete(settingsFile);
Directory.Delete(AppPaths.PluginDataFor("check-first"), recursive: true);
Directory.Delete(AppPaths.PluginDataFor("check-second"), recursive: true);
Directory.Delete(AppPaths.PluginDataFor("check-third"), recursive: true);

var slots = new ActivityArbiter(DiscordPresenceOutput.SlotOf);
slots.SetOrder(["check-watch", "check-compete", "check-play"]);
slots.Submit("check-play", 0, new PresenceActivity { Kind = ActivityKind.Playing });
slots.Submit("check-compete", 0, new PresenceActivity { Kind = ActivityKind.Competing });
slots.Submit("check-watch", 0, new PresenceActivity { Kind = ActivityKind.Watching });
Check(slots.Shown.Select(claim => claim.SourceId).SequenceEqual(["check-watch", "check-compete"]), "the top activity of each slot shows");
slots.Pin("check-play");
Check(slots.Shown.Select(claim => claim.SourceId).SequenceEqual(["check-play", "check-watch"]), "a pin wins its slot only");
slots.SetOneAtATime(true);
Check(slots.Shown.Select(claim => claim.SourceId).SequenceEqual(["check-play"]), "one at a time shows only the top activity");

var games = new ActivityArbiter(DiscordPresenceOutput.SlotOf);
games.SetOrder(["check-watch", "check-first-game", "check-second-game"]);
games.Submit("check-first-game", 0, new PresenceActivity { Kind = ActivityKind.Playing });
games.Submit("check-second-game", 0, new PresenceActivity { Kind = ActivityKind.Playing });
games.Submit("check-watch", 0, new PresenceActivity { Kind = ActivityKind.Watching });
games.Foreground("check-second-game");
Check(games.Shown.Select(claim => claim.SourceId).SequenceEqual(["check-watch", "check-second-game"]), "the game in front beats the order");
games.Foreground("check-first-game");
games.Foreground("check-browser");
Check(games.Shown.Select(claim => claim.SourceId).SequenceEqual(["check-watch", "check-first-game"]), "the game last in front stays");
games.Pin("check-second-game");
Check(games.Shown.Select(claim => claim.SourceId).SequenceEqual(["check-second-game", "check-watch"]), "a pin beats the foreground");
games.Pin(null);
games.SetOneAtATime(true);
Check(games.Shown.Select(claim => claim.SourceId).SequenceEqual(["check-watch"]), "one at a time keeps a higher Watching plugin above the games");
games.SetOrder(["check-second-game", "check-watch", "check-first-game"]);
Check(games.Shown.Select(claim => claim.SourceId).SequenceEqual(["check-first-game"]), "one at a time puts the game in front at the highest game's place");
games.SetOneAtATime(false);
games.OtherGameInFront(42);
Check(games.Shown.Select(claim => claim.SourceId).SequenceEqual(["check-watch"]), "a game without a plugin in front hides the game cards");
games.Foreground("check-second-game");
Check(games.Shown.Select(claim => claim.SourceId).SequenceEqual(["check-second-game", "check-watch"]), "a game with a plugin back in front shows again");
games.OtherGameInFront(42);
games.Pin("check-first-game");
Check(games.Shown.Select(claim => claim.SourceId).SequenceEqual(["check-first-game", "check-watch"]), "a pin still shows over a game without a plugin");
games.Pin(null);
games.OtherGameClosed(42);
Check(games.Shown.Select(claim => claim.SourceId).SequenceEqual(["check-second-game", "check-watch"]), "closing that game brings back the game last in front");

const string detectable = """
    [{"name": "Shooter", "executables": [{"is_launcher": false, "name": "win64/shooter-win64-shipping.exe", "os": "win32"},
                                         {"is_launcher": true, "name": "shooterlauncher.exe", "os": "win32"},
                                         {"is_launcher": false, "name": "shooter", "os": "linux"}]},
     {"name": "Classic", "executables": [{"is_launcher": false, "name": ">classic.exe", "os": "win32"}]},
     {"name": "Blocky", "executables": [{"arguments": "-game blocky", "is_launcher": false, "name": ">javaw.exe", "os": "win32"}]},
     {"name": "Broken", "executables": null}, {"executables": [{"name": "nameless.exe", "os": "win32"}]}]
    """;
var detectableGames = await Crovus.RichPresence.DetectableGames.LoadAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(detectable)));
Check(detectableGames.Find(@"D:\Games\Shooter\Binaries\Win64\Shooter-Win64-Shipping.exe") == "Shooter", "a detectable game matches by its path ending");
Check(detectableGames.Find(@"D:\Games\Shooter\Binaries\XWin64\Shooter-Win64-Shipping.exe") is null, "a path ending only matches whole folders");
Check(detectableGames.Find(@"D:\Games\Shooter\ShooterLauncher.exe") is null, "a launcher is not a game");
Check(detectableGames.Find(@"C:\Classic\classic.exe") == "Classic" && detectableGames.Count == 2, "exact names match, and entries needing arguments or broken are skipped");

var discordStorage = Directory.CreateTempSubdirectory("aura-discord-checks-").FullName;
WriteLocalStorage(discordStorage, "https://discord.com", "RunningGameStore", """
    {"enableDetection": {"d:/games/shooter/binaries/win64/shooter-win64-shipping.exe:Shooter": false},
     "gameOverrides": {"c:/mods/javaw.exe:Blocks": {"exePath": "c:/mods/javaw.exe", "name": "Blocks", "add": true, "hidden": false}}}
    """);
var discordGames = new DiscordGames(discordStorage) { Detectable = detectableGames };
discordGames.Refresh();
Check(discordGames.Find(@"D:\Games\Shooter\Binaries\Win64\Shooter-Win64-Shipping.exe") is null, "a game with detection off in Discord doesn't count");
Check(discordGames.Find(@"C:\Classic\classic.exe") == "Classic", "a game Discord detects counts");
Check(discordGames.Find(@"C:\Mods\javaw.exe") == "Blocks", "a game added in Discord counts");
Directory.Delete(discordStorage, recursive: true);

var party = ActivityMapper.ToRichPresence(new PresenceActivity { State = "In a match", Party = new PresenceParty(3, 12) }).Party;
Check(party is { Id: null, CurrentSize: 3, MaxSize: 12 }, "a party size reaches Discord");
Check(ActivityMapper.ToRichPresence(new PresenceActivity { Party = new PresenceParty(5, 4) }).Party is null, "an overfull party is left out");
Check(ActivityMapper.ToRichPresence(new PresenceActivity { Party = new PresenceParty(0, 4) }).Party is null, "an empty party is left out");

Check(Aura.Presence.Services.AniList.AniListClient.TitlesMatch("Kashita Maryoku wa Revo Barai de Kyousei Choushuu",
    "Kashita Maryoku wa [Revo Barai] de Kyousei Choushuu", null), "an anilist title matches across punctuation");

Console.WriteLine("All host checks passed.");

static void WriteLocalStorage(string directory, string origin, string key, string json)
{
    var batch = new MemoryStream();
    batch.Write(BitConverter.GetBytes(1UL));
    batch.Write(BitConverter.GetBytes(1));
    batch.WriteByte(1);

    foreach (var slice in new[] { System.Text.Encoding.Latin1.GetBytes($"_{origin}\0\u0001{key}"), [1, .. System.Text.Encoding.Latin1.GetBytes(json)] })
    {
        for (var length = (uint)slice.Length; ; length >>= 7)
        {
            batch.WriteByte((byte)(length < 0x80 ? length : length & 0x7F | 0x80));

            if (length < 0x80)
                break;
        }

        batch.Write(slice);
    }

    File.WriteAllBytes(Path.Combine(directory, "000001.log"),
        [0, 0, 0, 0, .. BitConverter.GetBytes((ushort)batch.Length), 1, .. batch.ToArray()]);
}

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
