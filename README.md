# Stride MCP

Stride MCP follows the same split used by Blender integrations: an editor-side
plugin exposes a loopback TCP bridge, while a separate MCP process communicates
with AI clients over standard input/output (stdio). A .NET startup hook loads
the editor plugin into Game Studio so it can be used with different projects
without adding a reference to each game.

## Current capabilities

The available tools are:

- `get_project_info`: reports the open project path/name and asset count.
- `list_assets`: searches project assets by name, URL, or asset type.
- `list_scene_entities`: lists entities, IDs, parents, transforms, and
  components in the focused, open scene editor.
- `list_entity_components`: lists an entity's component types and writable
  property names.
- `create_entity`, `rename_entity`, and `set_entity_transform`: create and
  update entities in the focused scene.
- `add_component`, `set_component_property`, and `remove_component`: edit
  components in the focused scene. New components need a public
  parameterless constructor; `TransformComponent` cannot be removed.
- `build_game`: runs `dotnet build` on the current Stride project's matching
  `.csproj`, using Debug or Release.
- `execute_script`: compiles and runs C# in Game Studio, with access to the
  editor session through the `Session` global.

Every scene mutation and build displays the operation details in Game Studio
and requires the user to approve it. Scene modifications create Stride
undo/redo transactions. The scene tools target the scene in the focused,
already-open scene editor; open and focus the desired scene before using them.
Build output includes the exit code and captured compiler output.

Every script execution displays the entire source in Game Studio and requires
the user to approve it. Approved scripts are arbitrary code, run in the
Game Studio process with the current Windows user's permissions, and are not
sandboxed; they can access files, processes, and the network. They can also
hang or crash the editor. Use `await OnUiThreadAsync(() => ...)` for operations
that must run on Game Studio's UI thread.

For example, ask OpenCode to run this read-only script to get the open project
name:

```csharp
await OnUiThreadAsync(() => Session.CurrentProject?.Name ?? "No project is open")
```

Review the code in Game Studio and select **Run script** to proceed.

The editor bridge listens only on `127.0.0.1:9877`. The port differs from the
common Blender MCP port so both integrations can run at the same time.

Scene asset edits remain in Game Studio's unsaved state until saved there.
Save the scene in Game Studio before building if the build must include those
changes. Asset creation and launching the game are not exposed as dedicated
tools yet.

## Build

```powershell
dotnet build .\StrideMcpPlugin.Editor.csproj -p:OutDir=.\bin\Debug\net10.0-windows-opencode-entities\
dotnet build .\StrideMcpBootstrap\StrideMcpBootstrap.csproj -p:OutDir=.\StrideMcpBootstrap\bin\Debug\net10.0-opencode-entities\
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
`%USERPROFILE%\.config\opencode\opencode.json` can register this MCP server.
The configuration launches `StrideMcpServer.dll`; Game Studio must separately
be started with `Start-StrideWithMcp.ps1` so the editor-side bridge is active.
After changing the OpenCode configuration, restart OpenCode and enable the
`stride` MCP server if it is not already enabled.

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

The read-only tools do not require confirmation. Script execution requires
explicit approval in Game Studio for every call.
