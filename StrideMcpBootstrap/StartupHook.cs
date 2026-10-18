using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

public static class StartupHook
{
    private const string EditorAssemblyName = "Stride.Core.Assets.Editor";
    private static readonly object SyncRoot = new();
    private static AssemblyDependencyResolver? _dependencyResolver;
    private static bool _pluginLoaded;

    public static void Initialize()
    {
        AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
        TryLoadPluginFromLoadedAssemblies();
    }

    private static void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs eventArgs)
    {
        var name = eventArgs.LoadedAssembly.GetName().Name ?? string.Empty;
        if (name == EditorAssemblyName)
        {
            try
            {
                var assembly = eventArgs.LoadedAssembly;
                var context = AssemblyLoadContext.GetLoadContext(assembly);
                File.AppendAllText(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "stride", "stridemcp-plugin.log"),
                    $"[{DateTime.Now:HH:mm:ss.fff}] Editor assembly loaded: name={name}, context={context?.Name ?? "??"}, location={assembly.Location}{Environment.NewLine}");
            }
            catch
            {
            }

            TryLoadPlugin();
        }
    }

    private static void TryLoadPluginFromLoadedAssemblies()
    {
        if (AppDomain.CurrentDomain.GetAssemblies()
            .Any(assembly => assembly.GetName().Name == EditorAssemblyName))
        {
            TryLoadPlugin();
        }
    }

    private static void TryLoadPlugin()
    {
        lock (SyncRoot)
        {
            if (_pluginLoaded)
                return;

            AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
            string? pluginPath = Environment.GetEnvironmentVariable("STRIDE_MCP_PLUGIN_PATH");
            if (string.IsNullOrWhiteSpace(pluginPath) || !File.Exists(pluginPath))
            {
                Console.Error.WriteLine(
                    "Stride MCP startup hook could not find the plugin. Set STRIDE_MCP_PLUGIN_PATH to the editor plugin DLL.");
                return;
            }

            try
            {
                pluginPath = Path.GetFullPath(pluginPath);
                _dependencyResolver = new AssemblyDependencyResolver(pluginPath);

                AssemblyLoadContext.Default.Resolving += ResolvePluginDependency;
                AssemblyLoadContext.Default.ResolvingUnmanagedDll += ResolvePluginUnmanagedDependency;

                var pluginAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(pluginPath);
                _pluginLoaded = true;

                // Assembly loading alone does not run module initializers, so the
                // bootstrap invokes the plugin's entry point explicitly.
                var entryType = pluginAssembly.GetType("StrideMcpPlugin.Editor.McpPluginEntry");
                var initialize = entryType?.GetMethod(
                    "Initialize",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (initialize is not null)
                {
                    initialize.Invoke(null, null);
                    Console.Error.WriteLine("Stride MCP plugin entry point executed.");
                }
                else
                {
                    Console.Error.WriteLine(
                        "Stride MCP plugin entry point 'StrideMcpPlugin.Editor.McpPluginEntry.Initialize' was not found.");
                }

                Console.Error.WriteLine($"Stride MCP editor plugin loaded from '{pluginPath}'.");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Stride MCP editor plugin failed to load: {exception}");
            }
        }
    }

    private static Assembly? ResolvePluginDependency(
        AssemblyLoadContext context,
        AssemblyName assemblyName)
    {
        string? assemblyNamePart = assemblyName.Name;
        if (assemblyNamePart is not null &&
            (string.Equals(assemblyNamePart, "Microsoft.Build", StringComparison.OrdinalIgnoreCase) ||
             assemblyNamePart.StartsWith("Microsoft.Build.", StringComparison.OrdinalIgnoreCase)))
        {
            // Game Studio selects and loads its own MSBuild toolset. Resolving
            // these from the plugin's NuGet dependency graph can load a
            // different MSBuild.Framework build into the default context.
            return null;
        }

        string? dependencyPath = _dependencyResolver?.ResolveAssemblyToPath(assemblyName);
        return dependencyPath is null ? null : context.LoadFromAssemblyPath(dependencyPath);
    }

    private static IntPtr ResolvePluginUnmanagedDependency(
        Assembly assembly,
        string unmanagedDllName)
    {
        string? dependencyPath = _dependencyResolver?.ResolveUnmanagedDllToPath(unmanagedDllName);
        return dependencyPath is null ? IntPtr.Zero : NativeLibrary.Load(dependencyPath);
    }
}
