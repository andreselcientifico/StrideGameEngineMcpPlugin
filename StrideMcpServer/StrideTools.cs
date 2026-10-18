using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class StrideTools(StrideBridgeClient bridge)
{
    [McpServerTool, Description("Returns the currently open Stride project and the number of assets in the editor.")]
    public async Task<string> GetProjectInfo(CancellationToken cancellationToken)
    {
        JsonElement result = await bridge.SendAsync(
            "get_project_info",
            new { },
            cancellationToken);
        return JsonSerializer.Serialize(result);
    }

    [McpServerTool, Description("Lists project assets visible to the Stride editor. Optionally filter by name, URL, or asset type.")]
    public async Task<string> ListAssets(
        [Description("Optional case-insensitive text to match against asset name, URL, or type.")] string search = "",
        [Description("Maximum number of assets to return (1-500).")] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        JsonElement result = await bridge.SendAsync(
            "list_assets",
            new { search, limit },
            cancellationToken);
        return JsonSerializer.Serialize(result);
    }

    [McpServerTool, Description(
        "Runs arbitrary C# inside the open Stride Game Studio process. Game Studio displays the entire script and requires approval before every execution. Approved scripts are not sandboxed and have the Windows user's permissions.")]
    public async Task<string> ExecuteScript(
        [Description("The C# script to compile and run after the approval dialog is accepted in Game Studio. The script can access the editor session through the Session global.")] string script,
        CancellationToken cancellationToken = default)
    {
        JsonElement result = await bridge.SendAsync(
            "execute_script",
            new { script },
            cancellationToken);
        return JsonSerializer.Serialize(result);
    }

    [McpServerTool, Description("Lists entities in the currently focused open Stride scene editor. Includes stable IDs, transforms, parents, and component types.")]
    public async Task<string> ListSceneEntities(CancellationToken cancellationToken = default)
    {
        JsonElement result = await bridge.SendAsync("list_scene_entities", new { }, cancellationToken);
        return JsonSerializer.Serialize(result);
    }

    [McpServerTool, Description("Lists component types and public properties on an entity in the currently focused open scene.")]
    public async Task<string> ListEntityComponents(
        [Description("Exact entity name in the focused scene.")] string entityName,
        CancellationToken cancellationToken = default)
    {
        JsonElement result = await bridge.SendAsync(
            "list_entity_components",
            new { entityName },
            cancellationToken);
        return JsonSerializer.Serialize(result);
    }

    [McpServerTool, Description("Creates an entity in the currently focused scene, optionally as a child of another entity. Game Studio asks for approval.")]
    public async Task<string> CreateEntity(
        [Description("Unique name for the new entity.")] string name,
        [Description("Optional parent entity name; leave empty to create a scene-root entity.")] string parentName = "",
        CancellationToken cancellationToken = default)
    {
        JsonElement result = await bridge.SendAsync(
            "create_entity",
            new { name, parentName },
            cancellationToken);
        return JsonSerializer.Serialize(result);
    }

    [McpServerTool, Description("Renames an entity in the currently focused open scene. Game Studio asks for approval.")]
    public async Task<string> RenameEntity(
        [Description("Current exact entity name.")] string entityName,
        [Description("New unique entity name.")] string newName,
        CancellationToken cancellationToken = default)
    {
        JsonElement result = await bridge.SendAsync(
            "rename_entity",
            new { entityName, newName },
            cancellationToken);
        return JsonSerializer.Serialize(result);
    }

    [McpServerTool, Description("Updates selected translation, Euler rotation in degrees, and/or scale for an entity. Omitted values remain unchanged. Game Studio asks for approval.")]
    public async Task<string> SetEntityTransform(
        [Description("Exact entity name in the focused scene.")] string entityName,
        [Description("World-independent local position X, in scene units.")] float? positionX = null,
        [Description("Local position Y, in scene units.")] float? positionY = null,
        [Description("Local position Z, in scene units.")] float? positionZ = null,
        [Description("Euler rotation X in degrees. If any rotation axis is specified, unspecified axes are set to zero.")] float? rotationX = null,
        [Description("Euler rotation Y in degrees.")] float? rotationY = null,
        [Description("Euler rotation Z in degrees.")] float? rotationZ = null,
        [Description("Local scale X.")] float? scaleX = null,
        [Description("Local scale Y.")] float? scaleY = null,
        [Description("Local scale Z.")] float? scaleZ = null,
        CancellationToken cancellationToken = default)
    {
        JsonElement result = await bridge.SendAsync(
            "set_entity_transform",
            new
            {
                entityName,
                positionX,
                positionY,
                positionZ,
                rotationX,
                rotationY,
                rotationZ,
                scaleX,
                scaleY,
                scaleZ
            },
            cancellationToken);
        return JsonSerializer.Serialize(result);
    }

    [McpServerTool, Description("Adds a public parameterless Stride EntityComponent to an entity. Specify a fully qualified .NET component type when ambiguous. Game Studio asks for approval.")]
    public async Task<string> AddComponent(
        [Description("Exact entity name in the focused scene.")] string entityName,
        [Description("Component type name or fully qualified .NET type name, for example Stride.Engine.CameraComponent.")] string componentType,
        CancellationToken cancellationToken = default)
    {
        JsonElement result = await bridge.SendAsync(
            "add_component",
            new { entityName, componentType },
            cancellationToken);
        return JsonSerializer.Serialize(result);
    }

    [McpServerTool, Description("Sets a public writable property on a component. Pass value as its JSON value; vectors use {\"x\":0,\"y\":0,\"z\":0}. Game Studio asks for approval.")]
    public async Task<string> SetComponentProperty(
        [Description("Exact entity name in the focused scene.")] string entityName,
        [Description("Component short or fully qualified type name.")] string componentType,
        [Description("Public writable property name.")] string propertyName,
        [Description("JSON value to convert to the property's type.")] JsonElement value,
        CancellationToken cancellationToken = default)
    {
        JsonElement result = await bridge.SendAsync(
            "set_component_property",
            new { entityName, componentType, propertyName, value },
            cancellationToken);
        return JsonSerializer.Serialize(result);
    }

    [McpServerTool, Description("Removes a component from an entity; the required TransformComponent cannot be removed. Game Studio asks for approval.")]
    public async Task<string> RemoveComponent(
        [Description("Exact entity name in the focused scene.")] string entityName,
        [Description("Component short or fully qualified type name.")] string componentType,
        CancellationToken cancellationToken = default)
    {
        JsonElement result = await bridge.SendAsync(
            "remove_component",
            new { entityName, componentType },
            cancellationToken);
        return JsonSerializer.Serialize(result);
    }

    [McpServerTool, Description("Builds the .csproj for the currently open Stride project using dotnet build. Review the target and approve in Game Studio before it runs.")]
    public async Task<string> BuildGame(
        [Description("Build configuration: Debug or Release.")] string configuration = "Debug",
        CancellationToken cancellationToken = default)
    {
        JsonElement result = await bridge.SendAsync(
            "build_game",
            new { configuration },
            cancellationToken);
        return JsonSerializer.Serialize(result);
    }
}
