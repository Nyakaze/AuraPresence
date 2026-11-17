#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property PublishAot=false
#:project ../src/Aura.Presence.App/Aura.Presence.App.csproj

using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using Aura.Presence.App.Plugins;
using Aura.Presence.Sdk;

const string metaUrl = "https://example.org/repo/meta.json";
var sha = new string('a', 64);

var meta = Marketplace.Parse($$"""
{
  "name": "Example",
  "url": "https://example.org",
  "somethingNew": true,
  "plugins": [
    { "internalName": "Good.Plugin", "name": "Good", "version": "1.2.0", "sdkVersion": "1.0", "iconUrl": "icons/good.png",
      "downloadUrl": "dl/Good.Plugin-1.2.0.zip", "sha256": "{{sha}}", "size": 10, "uses": ["One"], "extra": 1 },
    { "internalName": "../Escape", "name": "Bad", "version": "1.0.0", "downloadUrl": "https://example.org/x.zip", "sha256": "{{sha}}" },
    { "internalName": "Plain.Http", "name": "Http", "version": "1.0.0", "downloadUrl": "http://example.org/x.zip", "sha256": "{{sha}}" },
    { "internalName": "No.Hash", "name": "NoHash", "version": "1.0.0", "downloadUrl": "https://example.org/x.zip" },
    { "internalName": "Bad.Version", "name": "BadVersion", "version": "soon", "downloadUrl": "https://example.org/x.zip", "sha256": "{{sha}}" }
  ]
}
""", metaUrl);

Check(meta.Name == "Example" && meta.Plugins.Count == 1, "invalid entries are dropped and unknown fields ignored");
var good = meta.Plugins[0];
Check(good.DownloadUrl == "https://example.org/repo/dl/Good.Plugin-1.2.0.zip", "relative download links resolve against meta.json");
Check(good.IconUrl == "https://example.org/repo/icons/good.png", "relative icon links resolve against meta.json");
Check(good.Uses.SequenceEqual(["One"]) && good.ShowsActivity, "uses and defaults come through");

var api = new Version(1, 2);
Check(PluginStore.IsCompatible("1.0", api) && PluginStore.IsCompatible("1.2", api), "older or equal API minors are compatible");
Check(!PluginStore.IsCompatible("1.3", api) && !PluginStore.IsCompatible("2.0", api) && !PluginStore.IsCompatible("0.9", api) && !PluginStore.IsCompatible(null, api),
    "newer minors, other majors and missing versions are not");

Check(Marketplace.CheckRepoUrl("http://example.org/meta.json", []) is not null, "custom repos must be https");
Check(Marketplace.CheckRepoUrl(Marketplace.OfficialUrl, []) is not null, "the official repo can't be added as a custom one");
Check(Marketplace.CheckRepoUrl("https://example.org/meta.json", ["HTTPS://EXAMPLE.ORG/meta.json"]) is not null, "duplicates are refused");
Check(Marketplace.CheckRepoUrl("https://example.org/meta.json", []) is null, "a new https link is fine");

RepoMeta Repo(params PluginManifest[] plugins) => new() { Name = "r", Plugins = plugins };
PluginManifest Entry(string name, string version, string sdk = "1.0") => new() { InternalName = name, Name = name, Version = version, SdkVersion = sdk };

var official = new RepoResult(Marketplace.OfficialUrl, true, Repo(Entry("Stremio", "1.0.0")), null);
var custom = new RepoResult("https://a.example/meta.json", false, Repo(Entry("STREMIO", "9.0.0"), Entry("Extra", "2.0.0", "1.1")), null);
var other = new RepoResult("https://b.example/meta.json", false, Repo(Entry("Extra", "3.0.0")), null);
var catalog = Marketplace.Merge([official, custom, other]);

Check(catalog.Count(entry => entry.Manifest.InternalName.Equals("stremio", StringComparison.OrdinalIgnoreCase)) == 1 &&
      catalog.Single(entry => entry.Manifest.InternalName.Equals("stremio", StringComparison.OrdinalIgnoreCase)).Repo.Official,
    "custom repos can't replace an official plugin");
Check(Marketplace.Merge([official with { Meta = null, Error = "down" }, custom]).Count == 0, "custom repos wait for the official repo");

var installedExtra = Entry("Extra", "1.0.0") with { InstalledFrom = custom.MetaUrl };
Check(Marketplace.UpdateFor(installedExtra, catalog, new Version(1, 1))?.Repo == custom, "updates come from the repo it was installed from");
Check(Marketplace.UpdateFor(installedExtra, catalog, new Version(1, 0)) is null, "an update needing a newer API isn't offered");
Check(Marketplace.UpdateFor(Entry("Stremio", "1.0.0") with { InstalledFrom = custom.MetaUrl }, catalog) is null, "no update when versions match");
Check(Marketplace.UpdateFor(Entry("Stremio", "0.9.0") with { InstalledFrom = custom.MetaUrl }, catalog)?.Repo.Official == true, "the official repo can always update");

var root = Path.Combine(Path.GetTempPath(), $"aura-market-checks-{Guid.NewGuid():N}");
var assemblyBytes = File.ReadAllBytes(typeof(IPresencePlugin).Assembly.Location);
var assemblyVersion = PluginManifest.Normalize(typeof(IPresencePlugin).Assembly.GetName().Version).ToString();

byte[] Zip(params (string Name, byte[] Bytes)[] entries)
{
    using var buffer = new MemoryStream();

    using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
    {
        foreach (var (name, bytes) in entries)
        {
            using var stream = archive.CreateEntry(name).Open();
            stream.Write(bytes);
        }
    }

    return buffer.ToArray();
}

PluginManifest For(byte[] zip, string version) =>
    Entry("Fake.Plugin", version) with { Sha256 = Convert.ToHexString(SHA256.HashData(zip)), InstalledFrom = custom.MetaUrl };

bool Refused(Action action)
{
    try
    {
        action();
        return false;
    }
    catch (Exception exception) when (exception is InvalidDataException or IOException)
    {
        return true;
    }
}

var zip = Zip(("Fake.Plugin.dll", assemblyBytes), ("Fake.Plugin.deps.json", "{}"u8.ToArray()));
Check(Refused(() => PluginStore.Install(zip, For(zip, assemblyVersion) with { Sha256 = sha }, root)), "a checksum mismatch is refused");
Check(Refused(() => PluginStore.Install(zip, For(zip, "9.9.9"), root)), "a dll whose version differs from the repo is refused");

var empty = Zip(("readme.txt", "hi"u8.ToArray()));
Check(Refused(() => PluginStore.Install(empty, For(empty, assemblyVersion), root)), "a zip without the plugin dll is refused");

var escape = Zip(("../escaped.dll", assemblyBytes));
Check(Refused(() => PluginStore.Install(escape, For(escape, assemblyVersion), root)) && !File.Exists(Path.Combine(root, "escaped.dll")),
    "zip entries can't leave the plugin folder");
Check(!Directory.Exists(root) || Directory.GetDirectories(root).Length == 0, "failed installs leave nothing behind");

var folder = PluginStore.Install(zip, For(zip, assemblyVersion), root);
Check(folder == Path.Combine(root, "Fake.Plugin", assemblyVersion), "installs land in <name>/<version>");
Check(PluginStore.Read(folder) is { InstalledFrom: var from, ScheduledForDeletion: false } && from == custom.MetaUrl, "the manifest records its source");
Check(PluginStore.Installed(root).Single().Folder == folder, "the install is listed");

var older = Path.Combine(root, "Fake.Plugin", "0.1.0");
Directory.CreateDirectory(older);
File.WriteAllBytes(Path.Combine(older, "Fake.Plugin.dll"), assemblyBytes);
PluginStore.Write(older, For(zip, "0.1.0"));
var broken = Path.Combine(root, "Fake.Plugin", "99.0.0");
Directory.CreateDirectory(broken);
Directory.CreateDirectory(Path.Combine(root, ".partial-leftover"));
Directory.CreateDirectory(Path.Combine(root, "Empty.Plugin", "1.0.0"));

var kept = PluginStore.Cleanup(root);
Check(kept.Single().Folder == folder, "cleanup keeps the newest complete version");
Check(!Directory.Exists(older) && !Directory.Exists(broken), "cleanup removes older and incomplete versions");
Check(!Directory.Exists(Path.Combine(root, ".partial-leftover")) && !Directory.Exists(Path.Combine(root, "Empty.Plugin")),
    "cleanup removes partial downloads and empty plugins");

PluginStore.ScheduleDeletion(folder);
Check(PluginStore.Installed(root).Count == 0, "a scheduled plugin no longer counts as installed");
Check(PluginStore.Install(zip, For(zip, assemblyVersion), root) == folder && PluginStore.Read(folder) is { ScheduledForDeletion: false },
    "installing it again before a restart reuses the folder");

PluginStore.ScheduleDeletion(folder);
Check(PluginStore.Cleanup(root).Count == 0 && !Directory.Exists(Path.Combine(root, "Fake.Plugin")), "cleanup deletes a scheduled plugin");

Directory.Delete(root, recursive: true);
Console.WriteLine("All marketplace checks passed.");

static void Check(bool condition, string what)
{
    if (condition)
        return;

    Console.Error.WriteLine($"FAILED: {what}");
    Environment.Exit(1);
}
