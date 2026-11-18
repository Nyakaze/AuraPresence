# Building a plugin for Aura

A plugin is a .NET class library that Aura loads into its own process. It reports what it sees, and Aura decides what Discord shows. This guide covers writing one, testing it, and publishing it in a repo of your own that people add to Aura's Marketplace.

The official plugins in [AuraPlugins](https://github.com/Nyakaze/AuraPlugins) are complete working examples.

## 1. Project setup

Plugins compile against the Aura API: `Aura.Presence.Sdk`, plus any `Aura.Presence.Services.*` library you use. There is no NuGet package. Clone [AuraPresence](https://github.com/Nyakaze/AuraPresence) next to your project, at the tag of the oldest Aura you want to support:

```
git clone --branch v1.2.0 https://github.com/Nyakaze/AuraPresence
```

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Version>1.0.0</Version>
    <EnableDynamicLoading>true</EnableDynamicLoading>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\AuraPresence\src\Aura.Presence.Sdk\Aura.Presence.Sdk.csproj" Private="false" ExcludeAssets="runtime" />
  </ItemGroup>

</Project>
```

- `Private="false" ExcludeAssets="runtime"` matters. Aura provides the SDK and the services at runtime and always hands every plugin its own copies of them, so a plugin never ships them.
- The assembly name is the plugin's **internal name**. It must use only ASCII letters, digits, `.`, `-` and `_`, and it must differ from every official plugin's.
- For Windows Runtime APIs (media controls, notifications, …), target `net10.0-windows10.0.19041.0` or lower. Aura shares its own `WinRT.Runtime` and `Microsoft.Windows.SDK.NET` with plugins. Leave them out of your build with the `LeaveOutAuraProvidedWindowsSdk` targets in AuraPlugins' [`plugins/Directory.Build.targets`](https://github.com/Nyakaze/AuraPlugins/blob/main/plugins/Directory.Build.targets).
- Other NuGet packages are fine. They go in your zip next to your dll.

## 2. A minimal plugin

```csharp
using Aura.Presence.Sdk;

namespace Acme.Aura.Notepad;

public sealed class NotepadPlugin : IPresencePlugin
{
    private CancellationTokenSource? _stop;
    private Task _loop = Task.CompletedTask;

    public string Id => "acme.notepad";

    public string DisplayName => "Notepad";

    public string Description => "Shows on Discord while Notepad is open.";

    public IReadOnlyList<PluginSetting> Settings =>
    [
        new TextSetting("process", "Process name", "notepad", Help: "Without .exe.")
    ];

    public Task StartAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = WatchAsync(context, context.Settings.Get("process", "notepad"), _stop.Token);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stop is null)
            return;

        await _stop.CancelAsync();
        await _loop.WaitAsync(cancellationToken);
        _stop.Dispose();
    }

    private static async Task WatchAsync(IPluginContext context, string process, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        var wasRunning = false;

        try
        {
            do
            {
                var processes = System.Diagnostics.Process.GetProcessesByName(process);
                var running = processes.Length > 0;

                foreach (var found in processes)
                    found.Dispose();

                if (running && !wasRunning)
                {
                    await context.Activity.ReportAsync(new PresenceActivity
                    {
                        Kind = ActivityKind.Playing,
                        Name = "Notepad",
                        Details = "Writing something",
                        StartedAt = DateTimeOffset.UtcNow
                    });
                }
                else if (!running && wasRunning)
                {
                    await context.Activity.ClearAsync();
                }

                context.ReportHealth(running ? PluginHealth.Healthy : PluginHealth.Waiting,
                    running ? "Notepad is open." : "Waiting for Notepad.");
                wasRunning = running;
            }
            while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            context.Log.Error("Watching Notepad stopped.", exception);
            context.ReportHealth(PluginHealth.Failed, exception.Message);
        }
    }
}
```

Aura loads every public, non-abstract class that implements `IPresencePlugin` and has a parameterless constructor.

## 3. Lifecycle rules

- **`StartAsync` must return quickly.** Aura starts plugins one at a time and waits for each one, so a loop inside `StartAsync` keeps every plugin after yours from starting. Do the setup, start your work in the background, and return.
- **The token passed to `StartAsync` only fires when Aura exits.** To know when you are being stopped, keep your own `CancellationTokenSource` and cancel it in `StopAsync`, as above.
- **`StopAsync` gets 5 seconds.** Aura calls it when the user turns the plugin off, changes one of its settings, updates or uninstalls it, and when Aura exits. Release everything here: timers, event handlers, sockets and HTTP clients. Afterwards Aura clears your activity and publishes `null` on the media bus for you.
- **Every restart creates a new instance** through the parameterless constructor. Keep the constructor cheap, read your settings in `StartAsync`, and don't keep state in static fields.
- **Aura only sees exceptions from `StartAsync`.** One thrown there turns the plugin Failed, with the exception's message. Anything that goes wrong in your background work is yours to catch, log and report through `ReportHealth`.
- **`Id` must never change.** It names your data folder and your saved settings, and it is how Aura remembers the user's order and on/off switch. Aura ignores a second plugin with an `Id` it already loaded.

## 4. Reporting activity

`context.Activity.ReportAsync(activity)` replaces your current activity, and `ClearAsync()` withdraws it. Every plugin's activity shows under Aura's own Discord app, so there is no app ID to pick and no assets to upload.

| `PresenceActivity` field | On Discord |
|---|---|
| `Kind` + `Name` | The status line: `Watching` + `Frieren` shows "Watching Frieren". Without a `Name` it shows "Playing Aura". |
| `Details` | The first line of the card. |
| `State` | The second line. |
| `LargeImage`, `SmallImage` | `Key` is an image URL (at most 256 characters). `Text` is its hover caption. |
| `StartedAt` | The elapsed timer. |
| `EndsAt` | With `StartedAt`, the time left. It is dropped if it isn't after `StartedAt`. |
| `Party` | "(3 of 12)" after the state line. `Size` must be at least 1 and `Max` at least `Size`, or it is left out. Since Aura 1.4. |
| `Buttons` | At most 2. The label is cut at 32 characters, and the URL must be http(s) with at most 512 characters. |

Text is trimmed and cut at 128 characters with "…". Empty text is left out.

**Which activity shows:** Aura shows one Playing (or Competing) and one Watching (or Listening) activity side by side, unless the user turns on "One activity at a time" in the tray menu (since Aura 1.4; before, only one showed). When several plugins report the same kind, one wins: a source pinned by the user first. After that comes the user's order (they drag plugins in the window, and `DefaultPriority` only sets where a new plugin starts), then the most recent report. Report when something changes, not on every poll. Discord accepts about 5 updates per 20 seconds, and Aura always sends the latest one.

## 5. Health

`context.ReportHealth(health, message)` sets the line the window shows for your plugin. Repeating the same health and message does nothing.

| Health | Meaning |
|---|---|
| `Waiting` | Running, nothing to show (the app you watch isn't open, or setup isn't done). This is the start state. |
| `Healthy` | Working. |
| `Degraded` | Running, but the data looks wrong, for example after the watched app changed. The tray icon gets an amber dot. Prefer this over showing wrong data. |
| `Failed` | Stopped with an error. Also gets the amber dot. |

## 6. Settings

`Settings` lists the fields the window draws on your plugin's page. The user's values are saved to `%LOCALAPPDATA%\AuraPresence\plugin-data\<Id>\settings.json`. Each change **restarts the plugin**, so a new instance reads the new value in `StartAsync`.

| Setting | Stored as | Notes |
|---|---|---|
| `TextSetting` | `string` | `Monospace` for IDs. `Secret` masks the value once it is saved, and clearing the field removes it. Saved 0.8 s after typing stops. |
| `ToggleSetting` | `bool` | |
| `NumberSetting` | `int` | Between `Minimum` and `Maximum`. `Unit` is shown after the field. |
| `ChoiceSetting` | `string` | One of `Options`. |
| `LinkSetting` | nothing | A button that opens a URL. `Url` gets the saved settings and runs at click time, so it can use a value the user just typed (a sign-in page with their client ID, for example). |

- Read a value with `context.Settings.Get(key, default)`, using the same `Key` and default as the setting.
- `context.Settings.Set(key, value)` stores your own values too, such as the signed-in user's name. It does not restart the plugin.
- `ShowWhen = settings => …` hides a setting until the saved values meet a condition. That turns the page into a guided setup: show step 2 only once step 1 is filled in.
- `Card = "Account"` groups settings that follow each other into one card. A different `Card` name starts a new one.

## 7. Media events

Besides Discord presence, plugins can share what is playing over `context.Media`. A source plugin publishes `NowPlaying` (title, season, episode, position, duration, state and IDs under `MediaIdKeys`), and `null` when playback stops. Helper plugins such as AniList Sync and Simkl Sync subscribe and act on it, whatever the source.

```csharp
_subscription = context.Media.Subscribe(update => OnMediaAsync(update));
```

- Subscribe in `StartAsync` and dispose the subscription in `StopAsync`.
- Handlers get every update from every source, your own included (`update.SourceId` is the publisher's `Id`). They run one after another, so return quickly and move slow work, such as a web request, to the background.
- A plugin that never sets a status returns `false` from `ShowsActivity`. The window then lists it under Helpers.

### Browser pages

A plugin for a website reads it through `context.Browser`, which talks to the Aura browser extension (Chrome, Edge and other Chromium browsers). The extension only supplies data; everything about the site lives in your plugin.

```csharp
var page = context.Browser.PagesOn("www.netflix.com").FirstOrDefault(page => page.Video is not null);
var json = page is null ? null : await context.Browser.FetchAsync(page, "/some/api?id=1", token);
```

- `PagesOn(host)` returns the open tabs on that exact host: URL, title, whether the tab is in front, and the playing `<video>`'s position, duration and paused state. Asking for a host is what makes the extension read it, so poll it (every couple of seconds) while your plugin runs. A host nobody asked for in a minute is dropped.
- `FetchAsync(page, path)` runs a GET for a path on the page's own site inside that tab, with the user's sign-in, and returns the body, or `null` if it failed.
- `IsConnected` says whether any browser has the extension running. Set `"requires": ["browserExtension"]` in your `plugin.json`, so the Marketplace says the plugin needs the extension and its page shows how to add it.

## 8. Shared services

Aura ships these libraries and shares them with plugins. Reference them the same way as the SDK (`services\Aura.Presence.Services.<Name>\Aura.Presence.Services.<Name>.csproj`, with `Private="false" ExcludeAssets="runtime"`).

| Library | What it does |
|---|---|
| `Aura.Presence.Services.AniList` | `AniListClient` looks anime up by title or MyAnimeList ID. `AniListAccount` reads and saves a signed-in user's list progress. |
| `Aura.Presence.Services.Cinemeta` | `CinemetaClient` looks movies and shows up by title (IMDb ID, poster) and finds episode titles. |
| `Aura.Presence.Services.Chromium` | `LocalStorageReader` reads a Chromium or WebView2 app's localStorage from disk. |

## 9. Files and logs

- `context.DataDirectory` is `%LOCALAPPDATA%\AuraPresence\plugin-data\<Id>\`. It survives updates and uninstalls, so a reinstall gets its settings back.
- `context.Log.Debug/Info/Warn/Error` writes to `%LOCALAPPDATA%\AuraPresence\logs\aura.log`, tagged with your `Id`. Aura also logs there when it loads, starts or stops your plugin.

## 10. Testing locally

1. Build your plugin.
2. Copy the build output into `<Aura folder>\plugins\<AssemblyName>\`, so that `<AssemblyName>.dll` sits directly in it. The Aura folder is where `AuraPresence.exe` is; hovering the version in the window's header shows it.
3. Quit Aura from the tray and start it again.

`aura.log` should show `Loaded plugin '<Id>'`. Plugins in this folder load before installed ones, and an installed plugin with the same `Id` is ignored, so a dev copy replaces an installed one while it is there.

## 11. Publishing in your own repo

A repo is one `meta.json` at an https URL. Users add that URL in Aura under **Marketplace → Repos → Add repo**. Aura warns them once that custom plugins run with full access to their PC.

### The zip

- The zip holds your build output at its root, with `<internalName>.dll` directly inside and no top folder. Use the output of `dotnet publish -c Release`.
- The dll's assembly version must equal `version` (major.minor.patch).
- Leave out `Aura.Presence.Sdk`, `Aura.Presence.Services.*`, `WinRT.Runtime` and `Microsoft.Windows.SDK.NET`.
- At most 100 MB.

### meta.json

```json
{
  "name": "Acme plugins",
  "url": "https://github.com/acme/aura-plugins",
  "plugins": [
    {
      "internalName": "Acme.Aura.Notepad",
      "name": "Notepad",
      "author": "Acme",
      "version": "1.0.0",
      "sdkVersion": "1.2",
      "description": "Shows on Discord while Notepad is open.",
      "category": "Tools",
      "uses": ["Whether notepad.exe is running"],
      "showsActivity": true,
      "iconUrl": "icons/notepad.png",
      "repoUrl": "https://github.com/acme/aura-plugins",
      "downloadUrl": "https://github.com/acme/aura-plugins/releases/download/Acme.Aura.Notepad-1.0.0/Acme.Aura.Notepad-1.0.0.zip",
      "sha256": "<SHA-256 of the zip, 64 hex characters>",
      "size": 12345
    }
  ]
}
```

| Field | |
|---|---|
| `name`, `url` | The repo's name in the Repos list, and its home page. |
| `internalName` | The assembly name. Required. An entry using an official plugin's name is skipped. |
| `name` | The display name. Required. |
| `version` | Required. A higher version than the installed one shows as an update. Aura checks at startup and every 24 hours, and the user clicks Update. |
| `sdkVersion` | The Aura API version you built against, as major.minor. Aura loads the plugin only when the major is the same and its own minor is at least this one. Without it the plugin never loads. |
| `downloadUrl` | The zip. Required, https. |
| `sha256`, `size` | The zip's checksum (required) and exact size in bytes. Aura refuses a download that is bigger than `size` or has a different checksum. |
| `iconUrl` | A square PNG, 128 px. |
| `description`, `category`, `uses`, `showsActivity`, `repoUrl`, `author` | Shown on the Marketplace. `uses` lists what the plugin reads or touches, so users know what they install. `category` is free text and fills the filter (empty means "Other"). |
| `requires` | What the plugin needs besides Aura. `["browserExtension"]` means the Aura browser extension; the Marketplace and the plugin's page then explain how to add it. |

Relative URLs resolve against the meta.json URL, and anything that isn't https is dropped. Aura skips an entry that is missing a required field and ignores unknown fields. Updates come only from the repo a plugin was installed from.

### Automating releases

AuraPlugins' tooling works for any GitHub repo. Copy `pack.cs`, `Directory.Build.props`, `plugins/Directory.Build.targets` and `.github/workflows/release.yml` from [AuraPlugins](https://github.com/Nyakaze/AuraPlugins), then:

- Put each plugin in `plugins/<AssemblyName>/` with its `.csproj` (with a `<Version>`), an `icon.png` and a `plugin.json` holding the Marketplace fields (`name`, `author`, `description`, `category`, `uses`, `requires`, `showsActivity`, `repoUrl`).
- In `pack.cs`, change the repo `name` (it says `"Aura"`).
- In `release.yml`, replace the checks step with your own checks, and set `AURA_REF` to the oldest Aura tag you support.
- Your repo's meta.json URL is then `https://raw.githubusercontent.com/<you>/<repo>/main/meta.json`.

Releasing is then bumping `<Version>` and pushing: the workflow builds each new version, creates a GitHub release with its zip, computes `sdkVersion`, `sha256` and `size`, and commits `meta.json`. `dotnet run pack.cs` locally is a dry run into `dist/`. Don't edit `meta.json` by hand, because `pack.cs` uses it to know which versions are already released.
