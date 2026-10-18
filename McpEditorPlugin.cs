#nullable enable

using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stride.Core;
using Stride.Core.Assets.Editor.Services;
using Stride.Core.Assets.Editor.ViewModel;
using Stride.Core.Diagnostics;
using Stride.Core.Reflection;

namespace StrideMcpPlugin.Editor;

public sealed class McpEditorPlugin : AssetsPlugin
{
    private const int Port = 9877;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private TcpListener? _listener;
    private CancellationTokenSource? _shutdown;
    private Task? _listenerTask;
    private SessionViewModel? _session;
    private ILogger? _logger;

    public override void InitializePlugin(ILogger logger)
    {
        _logger = logger;
        Log("InitializePlugin called.");
    }

    public override void InitializeSession(SessionViewModel session)
    {
        _session = session;
        _shutdown = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, Port);
        try
        {
            _listener.Start();
        }
        catch (Exception exception)
        {
            Log($"listener.Start() failed: {exception}");
            throw;
        }
        _listenerTask = ListenAsync(_listener, _shutdown.Token);
        Log("InitializeSession called; TCP listener started.");
    }

    private async Task ListenAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(cancellationToken);
                try
                {
                    await HandleClientAsync(client, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    _logger?.Error("A Stride MCP bridge client disconnected unexpectedly.", exception);
                    Log($"client handler error: {exception}");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger?.Error("The Stride MCP bridge stopped unexpectedly.", exception);
            Log($"listener loop died: {exception}");
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream);
        using var writer = new StreamWriter(stream) { AutoFlush = true, NewLine = "\n" };

        string? line = await reader.ReadLineAsync(cancellationToken);
        if (line is null)
            return;

        Log($"bridge request received: {line}");

        BridgeRequest? request = null;
        BridgeResponse response;
        try
        {
            request = JsonSerializer.Deserialize<BridgeRequest>(line, JsonOptions);
            if (request is null ||
                string.IsNullOrWhiteSpace(request.RequestId) ||
                string.IsNullOrWhiteSpace(request.Operation) ||
                request.Arguments.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("The bridge request is missing required fields.");
            }

            SessionViewModel session = _session
                ?? throw new InvalidOperationException("The Stride editor session is not available.");
            object data;
            if (request.Operation == "execute_script")
            {
                string script = GetRequiredString(request.Arguments, "script");
                bool approved = await session.Dispatcher.InvokeAsync(
                    () => StrideScriptRunner.ConfirmExecution(script),
                    cancellationToken);
                if (!approved)
                    throw new InvalidOperationException("Script execution was declined in Game Studio.");

                data = await StrideScriptRunner.ExecuteAsync(session, script, cancellationToken);
            }
            else if (request.Operation == "build_game")
            {
                string configuration = GetOptionalString(request.Arguments, "configuration") ?? "Debug";
                StrideBuildTarget target = await session.Dispatcher.InvokeAsync(
                    () => StrideBuildRunner.GetTarget(session, configuration),
                    cancellationToken);
                bool approved = await session.Dispatcher.InvokeAsync(
                    () => StrideScriptRunner.ConfirmOperation(
                        "Confirm Stride game build",
                        "Game Studio is requesting a .NET build. Review the project and configuration before approving.",
                        $"{target.Configuration}: {target.ProjectFile}"),
                    cancellationToken);
                if (!approved)
                    throw new InvalidOperationException("The game build was declined in Game Studio.");

                data = await StrideBuildRunner.BuildAsync(target, cancellationToken);
            }
            else if (RequiresSceneChangeApproval(request.Operation))
            {
                bool approved = await session.Dispatcher.InvokeAsync(
                    () => StrideScriptRunner.ConfirmOperation(
                        "Confirm Stride scene change",
                        "Review the requested scene edit. Game Studio will record the change in its undo/redo history where supported.",
                        DescribeSceneChange(request)),
                    cancellationToken);
                if (!approved)
                    throw new InvalidOperationException("The scene change was declined in Game Studio.");

                data = await session.Dispatcher.InvokeAsync(
                    () => ExecuteOperation(session, request),
                    cancellationToken);
            }
            else
            {
                data = await session.Dispatcher.InvokeAsync(
                    () => ExecuteOperation(session, request),
                    cancellationToken);
            }

            response = new BridgeResponse(request.RequestId, true, data, null);
        }
        catch (Exception exception)
        {
            _logger?.Error("A Stride MCP bridge request failed.", exception);
            Log($"bridge request failed: {exception}");
            response = new BridgeResponse(request?.RequestId ?? string.Empty, false, null, exception.Message);
        }

        await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions));
    }

    private static object ExecuteOperation(SessionViewModel session, BridgeRequest request)
    {
        switch (request.Operation)
        {
            case "get_project_info":
            {
                var project = session.CurrentProject;
                var assets = session.AllAssets.ToArray();
                return new
                {
                    projectOpen = project is not null,
                    projectName = project?.Name,
                    projectPath = project?.ProjectPath.ToString(),
                    solutionPath = session.SolutionPath.ToString(),
                    assetCount = assets.Length
                };
            }
            case "list_assets":
            {
                int limit = request.Arguments.TryGetProperty("limit", out var limitValue) &&
                            limitValue.TryGetInt32(out var requestedLimit)
                    ? Math.Clamp(requestedLimit, 1, 500)
                    : 100;
                string search = request.Arguments.TryGetProperty("search", out var searchValue)
                    ? searchValue.GetString() ?? string.Empty
                    : string.Empty;

                var assets = session.AllAssets
                    .Where(asset => string.IsNullOrWhiteSpace(search) ||
                                    asset.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                    asset.Url.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                    asset.AssetType.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
                    .Take(limit)
                    .Select(asset => new
                    {
                        asset.Name,
                        asset.Url,
                        assetType = asset.AssetType.FullName,
                        id = asset.Id.ToString()
                    })
                    .ToArray();

                return new { count = assets.Length, assets };
            }
            case "list_scene_entities":
                return StrideSceneOperations.ListEntities();
            case "list_entity_components":
                return StrideSceneOperations.ListComponents(
                    GetRequiredString(request.Arguments, "entityName"));
            case "create_entity":
                return StrideSceneOperations.CreateEntity(
                    GetRequiredString(request.Arguments, "name"),
                    GetOptionalString(request.Arguments, "parentName"));
            case "rename_entity":
                return StrideSceneOperations.RenameEntity(
                    GetRequiredString(request.Arguments, "entityName"),
                    GetRequiredString(request.Arguments, "newName"));
            case "set_entity_transform":
                return StrideSceneOperations.SetTransform(
                    GetRequiredString(request.Arguments, "entityName"),
                    GetOptionalFloat(request.Arguments, "positionX"),
                    GetOptionalFloat(request.Arguments, "positionY"),
                    GetOptionalFloat(request.Arguments, "positionZ"),
                    GetOptionalFloat(request.Arguments, "rotationX"),
                    GetOptionalFloat(request.Arguments, "rotationY"),
                    GetOptionalFloat(request.Arguments, "rotationZ"),
                    GetOptionalFloat(request.Arguments, "scaleX"),
                    GetOptionalFloat(request.Arguments, "scaleY"),
                    GetOptionalFloat(request.Arguments, "scaleZ"));
            case "add_component":
                return StrideSceneOperations.AddComponent(
                    GetRequiredString(request.Arguments, "entityName"),
                    GetRequiredString(request.Arguments, "componentType"));
            case "set_component_property":
                return StrideSceneOperations.SetComponentProperty(
                    GetRequiredString(request.Arguments, "entityName"),
                    GetRequiredString(request.Arguments, "componentType"),
                    GetRequiredString(request.Arguments, "propertyName"),
                    GetRequiredValue(request.Arguments, "value"));
            case "remove_component":
                return StrideSceneOperations.RemoveComponent(
                    GetRequiredString(request.Arguments, "entityName"),
                    GetRequiredString(request.Arguments, "componentType"));
            default:
                throw new InvalidOperationException($"Unsupported bridge operation '{request.Operation}'.");
        }
    }

    private static bool RequiresSceneChangeApproval(string operation) =>
        operation is "create_entity" or "rename_entity" or "set_entity_transform" or
            "add_component" or "set_component_property" or "remove_component";

    private static string DescribeSceneChange(BridgeRequest request)
    {
        string[] properties = request.Arguments.EnumerateObject()
            .Where(property => property.Name is not "requestId")
            .Select(property => $"{property.Name}: {property.Value.GetRawText()}")
            .ToArray();
        return $"{request.Operation}{Environment.NewLine}{string.Join(Environment.NewLine, properties)}";
    }

    private static string GetRequiredString(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"The bridge request requires a non-empty '{propertyName}' string.");
        }

        return value.GetString()!;
    }

    private static string? GetOptionalString(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"The bridge request property '{propertyName}' must be a string.");
        return value.GetString();
    }

    private static float? GetOptionalFloat(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetSingle(out float number) ||
            !float.IsFinite(number))
            throw new InvalidDataException($"The bridge request property '{propertyName}' must be a finite number.");
        return number;
    }

    private static JsonElement GetRequiredValue(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            throw new InvalidDataException($"The bridge request requires a non-null '{propertyName}' value.");
        return value;
    }

    public override void RegisterPrimitiveTypes(System.Collections.Generic.ICollection<Type> primitiveTypes)
    {
    }

    protected override void SessionDisposed(SessionViewModel session)
    {
        _session = null;
        _shutdown?.Cancel();
        _listener?.Stop();
        _shutdown?.Dispose();
        _shutdown = null;
        _listener = null;
        _listenerTask = null;
    }

    private sealed record BridgeRequest(string RequestId, string Operation, JsonElement Arguments);

    private sealed record BridgeResponse(string RequestId, bool Success, object? Data, string? Error);

    private static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "stride", "stridemcp-plugin.log");

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch
        {
            // ignore logging failures
        }
    }
}

/// <summary>
/// Entry point invoked explicitly by the StrideMcpBootstrap startup hook.
/// Module initializers are not executed when an assembly is loaded without
/// touching any of its members, so the bootstrap must call this directly.
/// </summary>
public static class McpPluginEntry
{
    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized)
            return;
        _initialized = true;

        Log("Plugin entry point invoked by bootstrap.");
        var pluginTypesAssembly = typeof(McpEditorPlugin).Assembly;
        var assetsPluginType = typeof(AssetsPlugin);
        Log($"AssetsPlugin (sobrescrito por el plugin) esta en: {assetsPluginType.Assembly.Location} (contexto: {AssemblyLoadContext.GetLoadContext(assetsPluginType.Assembly)?.Name ?? "Default"})");
        Log($"El plugin assembly fue cargado en el contexto: {AssemblyLoadContext.GetLoadContext(pluginTypesAssembly)?.Name ?? "Default"}");
        Log($"AssetsPlugin.RegisteredPlugins count: {AssetsPlugin.RegisteredPlugins.Count}");
        AssemblyRegistry.Register(pluginTypesAssembly, AssemblyCommonCategories.Assets);
        AssetsPlugin.RegisterPlugin(typeof(McpEditorPlugin));
        Log($"Tras RegisterPlugin: RegisteredPlugins count = {AssetsPlugin.RegisteredPlugins.Count}, tipos: [{string.Join(", ", AssetsPlugin.RegisteredPlugins.Select(p => p.GetType().FullName))}]");
    }

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "stride", "stridemcp-plugin.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch
        {
            // ignore logging failures
        }
    }
}
