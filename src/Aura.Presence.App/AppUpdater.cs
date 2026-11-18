using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Aura.Presence.App.Plugins;
using Aura.Presence.Sdk;

namespace Aura.Presence.App;

public sealed record AppRelease
{
    public string Version { get; init; } = "";

    public string? DownloadUrl { get; init; }

    public string? Sha256 { get; init; }

    public long Size { get; init; }

    public Version ParsedVersion => System.Version.TryParse(Version, out var version) ? PluginManifest.Normalize(version) : new Version();
}

public static class AppUpdater
{
    public const string LatestUrl = "https://github.com/Nyakaze/AuraPresence/releases/latest/download/update.json";
    public const string ExecutableName = "AuraPresence.exe";

    private const string OldSuffix = ".old";
    private const string StagingFolder = "update";

    private static readonly AppLog Log = AppLog.For("Updater");

    public static Version Current { get; } = PluginManifest.Normalize(typeof(AppUpdater).Assembly.GetName().Version);

    public static string InstallDirectory { get; } = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

    public static bool IsDevBuild { get; } =
        InstallDirectory.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    public static async Task<AppRelease?> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (IsDevBuild)
            return null;

        var release = Parse(await Marketplace.GetStringAsync(LatestUrl, cancellationToken));

        return release.ParsedVersion > Current ? release : null;
    }

    public static AppRelease Parse(string json)
    {
        var release = JsonSerializer.Deserialize<AppRelease>(json, PluginStore.Json) ?? throw new InvalidDataException("The update file is empty.");

        if (!System.Version.TryParse(release.Version, out _) ||
            !Uri.TryCreate(release.DownloadUrl, UriKind.Absolute, out var download) || download.Scheme != Uri.UriSchemeHttps ||
            release.Sha256 is not { Length: 64 })
            throw new InvalidDataException("The update file isn't valid.");

        return release;
    }

    public static async Task DownloadAndApplyAsync(AppRelease release, CancellationToken cancellationToken = default)
    {
        var zip = await Marketplace.DownloadAsync(release.DownloadUrl!, release.Size, cancellationToken);
        await Task.Run(() => Apply(zip, release, AppContext.BaseDirectory, Path.Combine(AppPaths.DataRoot, StagingFolder)), cancellationToken);
    }

    public static void Apply(byte[] zip, AppRelease release, string installDirectory, string stagingDirectory)
    {
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(zip)), release.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The download doesn't match the checksum of the release.");

        if (Directory.Exists(stagingDirectory))
            Directory.Delete(stagingDirectory, recursive: true);

        try
        {
            using (var stream = new MemoryStream(zip))
                ZipFile.ExtractToDirectory(stream, stagingDirectory);

            var assembly = Path.Combine(stagingDirectory, Path.ChangeExtension(ExecutableName, ".dll"));

            if (!File.Exists(Path.Combine(stagingDirectory, ExecutableName)) || !File.Exists(assembly))
                throw new InvalidDataException("The download isn't an Aura build.");

            if (PluginManifest.Normalize(AssemblyName.GetAssemblyName(assembly).Version) != release.ParsedVersion)
                throw new InvalidDataException($"The download isn't Aura {release.Version}.");

            foreach (var file in Directory.EnumerateFiles(stagingDirectory, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(installDirectory, Path.GetRelativePath(stagingDirectory, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                if (File.Exists(target))
                    File.Move(target, target + OldSuffix, overwrite: true);

                File.Copy(file, target);
            }

            Log.Info($"Aura {release.Version} is in place; restarting.");
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
                Directory.Delete(stagingDirectory, recursive: true);
        }
    }

    public static void RemoveOldFiles(string installDirectory)
    {
        foreach (var file in Directory.EnumerateFiles(installDirectory, "*" + OldSuffix, SearchOption.AllDirectories))
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Couldn't remove '{file}': {exception.Message}");
            }
        }
    }
}
