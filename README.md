# Stride MCP

This project is licensed under the [MIT License](LICENSE). You may use,
modify, and redistribute it, including as one of its creators, subject to
the license terms.

Stride MCP follows the same split used by Blender integrations: an editor-side
plugin exposes a loopback TCP bridge, while a separate MCP process communicates
with AI clients over standard input/output (stdio). A .NET startup hook loads
the editor plugin into Game Studio so it can be used with different projects
without adding a reference to each game.

## MCP capabilities

The `stride` MCP server exposes the following tools:

| Tool | What it does | Inputs and constraints |
| --- | --- | --- |
| `get_project_info` | Returns the open project name/path, solution path, and asset count. | None. |
| `list_assets` | Searches assets by name, URL, or type. | Optional `search`; `limit` 1-500 (default 100). |
| `list_scene_entities` | Lists entities in the focused open scene, including IDs, parents, transforms, and component types. | Open and focus the intended scene first. |
| `list_entity_components` | Lists component types and public properties for an entity. | Exact, unambiguous `entityName`. |
| `create_entity` | Creates a scene-root entity or a child entity. | Unique `name`; optional `parentName`. |
| `rename_entity` | Renames an entity. | Exact `entityName`; unique `newName`. |
| `set_entity_transform` | Changes local position, rotation, and/or scale. | Entity name plus any position/rotation/scale axes. Rotation uses Euler degrees; if one rotation axis is supplied, other omitted axes become zero. Omitted position/scale axes stay unchanged. |
| `add_component` | Adds a Stride component to an entity. | Component type must be loaded, concrete, and have a public parameterless constructor. Use a fully-qualified name if ambiguous. |
| `set_component_property` | Changes a public writable component property. | Entity, component type, property name, and a JSON value. Vectors use `{ "x": 0, "y": 0, "z": 0 }`; quaternions also need `w`. |
| `remove_component` | Removes a component from an entity. | Entity and component type. The required `TransformComponent` cannot be removed. |
| `build_game` | Runs `dotnet build` for the current Stride project. | `configuration`: `Debug` or `Release` (default `Debug`). Returns exit code and build output; times out after 10 minutes. |
| `execute_script` | Compiles and runs C# inside Game Studio with access to `Session`. | C# `script` up to 128 KiB. Arbitrary code; see security warning below. |

For AI tool selection, step-by-step workflows, and safety requirements, see
[the Stride MCP guide for AI assistants](STRIDE-MCP-GUIDE.md).

### Scene editing workflow

Open and focus the intended scene in Game Studio, call `list_scene_entities`,
then inspect the target with `list_entity_components` before changing a
component. Each scene change requires approval in Game Studio and uses Stride's
undo/redo transactions where supported. Verify changes by listing the scene
again. Scene changes remain unsaved until saved in Game Studio.

To include scene changes in a build, save the scene manually first. Then call
`build_game` and approve the selected project/configuration in Game Studio.
The build does not save the scene automatically.

### Script safety

`execute_script` is a last resort when a dedicated tool cannot accomplish the
task. Every script is shown in full and requires approval in Game Studio.
Approved code is **not sandboxed**: it runs as the current Windows user and can
access files, processes, and the network, or hang/crash the editor. Use
`await OnUiThreadAsync(() => ...)` for APIs that require Game Studio's UI
thread.

OpenCode's global `opencode.json` is configured to load this guide
automatically. Restart OpenCode after changing its configuration.

The editor bridge listens only on `127.0.0.1:9877`. The port differs from the
common Blender MCP port so both integrations can run at the same time.

Scene asset edits remain in Game Studio's unsaved state until saved there.
Save the scene in Game Studio before building if the build must include those
changes. There is no dedicated delete-entity, asset-creation, scene-save, or
game-run tool yet.

## Build

```powershell
dotnet build .\StrideMcpPlugin.Editor.csproj -p:OutDir=.\bin\Debug\net10.0-windows-opencode-entities\
dotnet build .\StrideMcpBootstrap\StrideMcpBootstrap.csproj -p:OutDir=.\StrideMcpBootstrap\bin\Debug\net10.0-opencode-msbuild-fix\
dotnet build .\StrideMcpServer\StrideMcpServer.csproj -p:OutDir=.\StrideMcpServer\bin\Debug\net10.0-opencode-entities\
```

## Launch Game Studio with the plugin

Close any already-running Game Studio instance, then launch it through the
workspace script so the startup hook and plugin assembly are loaded:

```powershell
.\Start-StrideWithMcp.ps1 `
  -GameStudioPath "C:\path\to\Stride.GameStudio.exe" `
  -SolutionPath "C:\path\to\game.slnx"
```

The script builds the editor plugin and startup hook into dedicated output
folders, then starts Game Studio with the supplied solution. After stopping
OpenCode, build the MCP server separately using the command in the Build
section, then restart OpenCode. The script does not modify the Stride NuGet
package or the game project. The MCP client configuration is in
`.vscode\mcp.json`.

## OpenCode

The global OpenCode configuration at
`%USERPROFILE%\.config\opencode\opencode.json` registers this MCP server.
The configuration launches `StrideMcpServer.dll`; Game Studio must separately
be started with `Start-StrideWithMcp.ps1` so the editor-side bridge is active.
If OpenCode was already running when its configuration changed, restart it and
confirm that the `stride` MCP server is enabled.

The configured instruction entry is:

```json
"instructions": [
  "D:\\Documentos\\StrideMcpPlugin\\STRIDE-MCP-GUIDE.md"
]
```

If you move the plugin folder, update this absolute path. The guide tells the
model to use these instructions only when Stride and its MCP server are
relevant.

### Game Studio startup: MSBuild compatibility

The plugin's Roslyn dependencies include MSBuild assemblies for IDE/project
workspace support. The startup hook deliberately does not resolve any
`Microsoft.Build.*` assembly from the plugin's dependency graph: Stride must
select the MSBuild toolset from the installed .NET SDK itself. If Game Studio
reports an `IEventSource5` `TypeLoadException`, close it and restart with the
updated launcher/plugin. This commonly happens when the plugin's MSBuild 17
dependency is loaded before the .NET 10 SDK's MSBuild 18; both assemblies
report assembly version `15.1.0.0`, so the exception can misleadingly look
like an SDK installation problem. Confirm `dotnet --list-sdks` includes a
.NET 10 SDK; the plugin must not replace the SDK's MSBuild assemblies.

## VS Code MCP configuration

The workspace `.vscode\mcp.json` starts the stdio MCP server. Start Game Studio
with the script above, then use the MCP tools to query that live editor session.
Only one Stride Game Studio session can use the fixed bridge port at a time.

The read-only inspection tools do not require confirmation. Scene changes,
builds, and script execution require explicit approval in Game Studio for
every call.
