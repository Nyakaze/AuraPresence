using System.Reflection;
using System.Runtime.Loader;

namespace Aura.Presence.App.Plugins;

public sealed class PluginLoadContext : AssemblyLoadContext
{
    private const string ServicesPrefix = "Aura.Presence.Services.";

    private static readonly HashSet<string> SharedAssemblies =
    [
        typeof(Sdk.IPresencePlugin).Assembly.GetName().Name!,
        "WinRT.Runtime",
        "Microsoft.Windows.SDK.NET"
    ];

    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginAssemblyPath)
        : base(Path.GetFileNameWithoutExtension(pluginAssemblyPath), isCollectible: false)
    {
        _resolver = new AssemblyDependencyResolver(pluginAssemblyPath);
    }

    public static bool IsShared(string? assemblyName) =>
        assemblyName is not null &&
        (SharedAssemblies.Contains(assemblyName) || assemblyName.StartsWith(ServicesPrefix, StringComparison.Ordinal));

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (IsShared(assemblyName.Name))
            return Default.LoadFromAssemblyName(new AssemblyName(assemblyName.Name!));

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
