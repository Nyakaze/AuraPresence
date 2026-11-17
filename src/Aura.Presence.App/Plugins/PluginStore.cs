using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Aura.Presence.Sdk;

namespace Aura.Presence.App.Plugins;

public sealed record PluginManifest
{
    public string InternalName { get; init; } = "";

    public string Name { get; init; } = "";

    public string? Author { get; init; }

    public string Version { get; init; } = "";

    public string? SdkVersion { get; init; }

    public string? Description { get; init; }

    public string? Category { get; init; }

    public IReadOnlyList<string> Uses { get; init; } = [];

    public bool ShowsActivity { get; init; } = true;

    public string? IconUrl { get; init; }

    public string? RepoUrl { get; init; }

    public string? DownloadUrl { get; init; }

    public string? Sha256 { get; init; }

    public long Size { get; init; }

    public string? InstalledFrom { get; init; }

    public bool ScheduledForDeletion { get; init; }

    public Version ParsedVersion => System.Version.TryParse(Version, out var version) ? Normalize(version) : new Version();

    public static Version Normalize(Version? version) =>
        version is null ? new Version() : new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
}

public sealed record InstalledPackage(string Folder, PluginManifest Manifest)
{
    public string AssemblyPath => Path.Combine(Folder, Manifest.InternalName + ".dll");
}

public static class PluginStore
{
    public const string ManifestFile = "manifest.json";

    private const string PartialPrefix = ".partial-";

    private static readonly AppLog Log = AppLog.For("PluginStore");

    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static Version ApiVersion { get; } = typeof(IPresencePlugin).Assembly.GetName().Version ?? new Version(1, 0);

    public static bool IsCompatible(string? sdkVersion, Version? api = null)
    {
        api ??= ApiVersion;

        return Version.TryParse(sdkVersion, out var needed) && needed.Major == api.Major && needed.Minor <= api.Minor;
    }

    public static bool IsValidName(string? internalName) =>
        !string.IsNullOrWhiteSpace(internalName) && internalName.Trim('.').Length > 0 &&
        internalName.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    public static string? FolderOf(IPresencePlugin plugin) => Path.GetDirectoryName(plugin.GetType().Assembly.Location);

    public static PluginManifest? Read(string folder)
    {
        var file = Path.Combine(folder, ManifestFile);

        if (!File.Exists(file))
            return null;

        try
        {
            return JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(file), Json);
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            Log.Warn($"Unreadable manifest '{file}': {exception.Message}");
            return null;
        }
    }

    public static void Write(string folder, PluginManifest manifest) =>
        File.WriteAllText(Path.Combine(folder, ManifestFile), JsonSerializer.Serialize(manifest, Json));

    public static void ScheduleDeletion(string folder)
    {
        if (Read(folder) is { } manifest)
            Write(folder, manifest with { ScheduledForDeletion = true });
    }

    public static IReadOnlyList<InstalledPackage> Installed(string root) =>
        [.. Names(root).Select(Newest).OfType<InstalledPackage>().Where(package => !package.Manifest.ScheduledForDeletion)];

    public static IReadOnlyList<InstalledPackage> Cleanup(string root)
    {
        if (!Directory.Exists(root))
            return [];

        foreach (var partial in Directory.EnumerateDirectories(root, PartialPrefix + "*"))
            Delete(partial);

        var kept = new List<InstalledPackage>();

        foreach (var nameFolder in Names(root))
        {
            var newest = Newest(nameFolder);

            foreach (var versionFolder in Directory.EnumerateDirectories(nameFolder))
            {
                if (newest is null || versionFolder != newest.Folder || newest.Manifest.ScheduledForDeletion)
                    Delete(versionFolder);
            }

            if (newest is { Manifest.ScheduledForDeletion: false })
                kept.Add(newest);
            else
                Delete(nameFolder);
        }

        return kept;
    }

    public static string Install(byte[] zip, PluginManifest manifest, string root)
    {
        if (!IsValidName(manifest.InternalName))
            throw new InvalidDataException($"'{manifest.InternalName}' isn't a valid plugin name.");

        if (!string.Equals(Convert.ToHexString(SHA256.HashData(zip)), manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The download doesn't match the checksum in the repo.");

        var target = Path.Combine(root, manifest.InternalName, manifest.ParsedVersion.ToString());

        if (Read(target) is { } existing && File.Exists(Path.Combine(target, manifest.InternalName + ".dll")))
        {
            Write(target, existing with { ScheduledForDeletion = false, InstalledFrom = manifest.InstalledFrom });
            return target;
        }

        var partial = Path.Combine(root, PartialPrefix + Guid.NewGuid().ToString("N"));

        try
        {
            using (var stream = new MemoryStream(zip))
                ZipFile.ExtractToDirectory(stream, partial);

            var assembly = Path.Combine(partial, manifest.InternalName + ".dll");

            if (!File.Exists(assembly))
                throw new InvalidDataException($"The download has no {manifest.InternalName}.dll.");

            if (PluginManifest.Normalize(AssemblyName.GetAssemblyName(assembly).Version) != manifest.ParsedVersion)
                throw new InvalidDataException($"The download isn't version {manifest.Version}.");

            Write(partial, manifest with { ScheduledForDeletion = false });

            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.Move(partial, target);

            return target;
        }
        finally
        {
            if (Directory.Exists(partial))
                Directory.Delete(partial, recursive: true);
        }
    }

    private static IEnumerable<string> Names(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateDirectories(root).Where(folder => !Path.GetFileName(folder).StartsWith(PartialPrefix, StringComparison.Ordinal))
            : [];

    private static InstalledPackage? Newest(string nameFolder)
    {
        var name = Path.GetFileName(nameFolder);

        return Directory.EnumerateDirectories(nameFolder)
            .Select(folder => (Folder: folder, Manifest: Read(folder)))
            .Where(candidate => candidate.Manifest is { } manifest &&
                                string.Equals(manifest.InternalName, name, StringComparison.OrdinalIgnoreCase) &&
                                File.Exists(Path.Combine(candidate.Folder, manifest.InternalName + ".dll")))
            .Select(candidate => new InstalledPackage(candidate.Folder, candidate.Manifest!))
            .MaxBy(package => package.Manifest.ParsedVersion);
    }

    private static void Delete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
            Log.Info($"Removed '{folder}'.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Couldn't remove '{folder}': {exception.Message}");
        }
    }
}
