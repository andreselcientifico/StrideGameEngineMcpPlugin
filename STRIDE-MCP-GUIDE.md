# Stride MCP instructions for AI assistants

Use these instructions only when the user is working with Stride Game Studio
and the `stride` MCP server is available. Prefer the dedicated MCP tools below
over searching the plugin source or asking the user to write C#.

> **Verified 2026-10-17:** everything in the "Verified capabilities and
> patterns" section at the bottom was tested live against Stride Game Studio
> 4.4.1 (.NET 10). It includes the direct bridge helper tools, working code
> patterns for advanced scene mutation, procedural model assets, and known
> pitfalls (read it before doing anything the dedicated tools cannot do).

## First steps

1. Call `get_project_info` to confirm the live Stride project is connected.
2. For asset questions, use `list_assets` with a specific search and a
   reasonable limit.
3. Before scene work, ask the user to open and focus the intended scene in
   Game Studio, then call `list_scene_entities`. Never guess which open scene
   the editor targets.
4. If a tool reports a missing connection, scene, or project, explain that
   Game Studio must be open with the MCP plugin and avoid retry loops.

## Tool selection

| User intent | MCP tool | Important inputs and behavior |
| --- | --- | --- |
| Inspect the live project | `get_project_info` | No inputs. |
| Find project assets | `list_assets` | Optional case-insensitive `search`; `limit` is 1-500. |
| Inspect the focused scene hierarchy | `list_scene_entities` | No inputs; returns names, IDs, parents, transforms, and component types. |
| Inspect components/properties | `list_entity_components` | Exact, unambiguous `entityName`; reports property names, types, and writability. |
| Create an entity | `create_entity` | Unique `name`; optional `parentName`; empty parent creates a scene-root entity. |
| Rename an entity | `rename_entity` | Exact current `entityName` and unique `newName`. |
| Change transform | `set_entity_transform` | Exact `entityName`; optional position, rotation, and scale axes. At least one value is required. |
| Add a component | `add_component` | Exact `entityName`; `componentType` must resolve to a loaded concrete `EntityComponent` with a public parameterless constructor. Use its fully qualified name if ambiguous. |
| Change a component property | `set_component_property` | Exact entity, component type, public writable `propertyName`, and JSON `value`. |
| Remove a component | `remove_component` | Exact entity and component type. `TransformComponent` is required and cannot be removed. |
| Build the game | `build_game` | `configuration`: `Debug` (default) or `Release`; builds the current project's selected `.csproj`. |
| Run advanced editor automation | `execute_script` | C# string `script`; use only when dedicated tools cannot accomplish the requested task. |

## Scene editing workflow

1. Inspect the focused scene with `list_scene_entities`.
2. Inspect the target entity using `list_entity_components` before changing
   component properties. Match the component and property type to the returned
   metadata; do not invent member names.
3. Make the smallest change using the dedicated mutation tool.
4. Wait for the user to approve the operation in Game Studio, then inspect the
   scene again to verify it.
5. For rotation, specify Euler angles in degrees. If any rotation axis is
   supplied, omitted rotation axes become zero. Omitted position and scale
   axes keep their current values.
6. JSON vectors use `{"x": 0, "y": 0, "z": 0}`. Quaternion properties use
   `{"x": 0, "y": 0, "z": 0, "w": 1}`.
7. Scene changes are undoable in Stride where supported, but remain unsaved
   until the user saves the scene in Game Studio. There is no dedicated
   `delete_entity`, asset-creation, game-run, or scene-save tool.

Every dedicated scene mutation displays its operation details in Game Studio
and requires the user to approve it. Do not claim that an operation succeeded
until the MCP tool returns success. Do not hide or bypass the confirmation.

## Building

Before calling `build_game`, identify the active project and requested
configuration. The tool shows the selected `.csproj` and configuration in
Game Studio and requires approval. It returns the exit code and compiler
output; report build failures clearly. The build does not save the open scene:
ask the user to save in Game Studio first if the build must include scene
changes. Builds time out after ten minutes.

## C# script safety

`execute_script` runs arbitrary C# in the Game Studio process as the current
Windows user. It is **not sandboxed** and can read/write files, access the
network, start processes, modify assets, or hang/crash Game Studio. Each
request shows its entire script and requires the user's explicit approval
inside Game Studio. That dialog is not a sandbox or an automatic approval.

- Prefer dedicated MCP tools; use scripts only for operations those tools do
  not support.
- Explain why a script is needed and describe any side effects before asking
  the user to approve it.
- Never execute or propose destructive, external, or security-sensitive script
  actions unless the user explicitly requested that effect.
- Keep scripts short and narrowly scoped. Script input is limited to 128 KiB.
- Use `Session` to access the open Stride session.
- Use `await OnUiThreadAsync(() => ...)` for Game Studio UI/editor APIs that
  require the UI thread.
- Return simple values (strings or primitives) for useful script results.

## Connection requirements

Game Studio must be launched with `Start-StrideWithMcp.ps1`; the MCP server
alone cannot inspect or control the editor. OpenCode's `stride` MCP server
communicates over stdio with that server, which reaches Game Studio over
loopback `127.0.0.1:9877`. Only one Game Studio session can occupy that bridge
port at a time.

## Verified capabilities and patterns (tested on Game Studio 4.4.1 / .NET 10)

### Direct bridge helper tools (when MCP tools are not loaded in the session)

If the `stride` MCP tools are not available in the current OpenCode session
(for example because the session started before the MCP server was enabled),
you can talk to the editor bridge directly over TCP with the workspace helpers
in `D:\Documentos\StrideMcpPlugin`:

- `Send-StrideBridge.ps1 -Operation <op> -ArgumentsJson '<json>'` — any
  dedicated-tool operation with the same operation names listed above.
- `Send-StrideScript.ps1 -ScriptFile <file>` — sends a C# script from a file
  as `execute_script`. Write scripts to `Scripts\*.ps1.txt` and keep them
  reusable.

Protocol (for debugging or re-implementing): newline-terminated JSON per
connection, one request per connection:

```json
{"requestId":"<guid>","operation":"<op>","arguments":{...}}
{"requestId":"<guid>","success":true,"data":{...},"error":null}
```

- Bridge address is `127.0.0.1:9877` (fixed).
- Plugin log: `%LOCALAPPDATA%\stride\stridemcp-plugin.log`.
- PowerShell quoting pitfall: inside `-ArgumentsJson` use PowerShell single
  quotes with doubled inner quotes (`'{""search"":""salt""}'`) or build the
  JSON with `ConvertTo-Json`. `\"` escaping produces invalid JSON.
- Batching several scene mutations into ONE script shows ONE approval dialog
  and one undo step; prefer that over many small calls.

### Working script patterns (verified inside `execute_script`)

Locating the focused scene editor — the plugin walks BOTH the logical and
visual WPF trees from `Application.Current.Windows` looking for
`Stride.Assets.Presentation.AssetEditors.SceneEditor.ViewModels.SceneEditorViewModel`
(the visual-tree pass is required: asset editors live in data templates that
only appear in the visual tree):

```csharp
// Collect all windows, walk with VisualTreeHelper + LogicalTreeHelper,
// pushing every FrameworkElement.DataContext, until a SceneEditorViewModel
// is found. Reuse this walk at the top of every scene script.
```

Reaching entities — from the editor:

- `editor.HierarchyRoot` (`EntityHierarchyRootViewModel`) — recurse its
  `TransformChildren` (each `EntityViewModel` exposes `Name`, `Components`,
  and `AssetSideEntity` — the design-side `Stride.Engine.Entity`).
- `editor.Asset` — the scene `AssetItem`/graph; `editor.UndoRedoService` —
  wrap ALL changes in `CreateTransaction()` and call `SetName(transaction, ...)`.
- `editor.Session.AssetNodeContainer` — Quantum node container; pattern
  `nodeContainer.GetNode(obj)["PropertyName"].Update(value)` for property
  writes that need undo tracking.

Creating entities by script (mirrors `create_entity`, but supports custom
components in one step):

```csharp
var entity = new Stride.Engine.Entity { Name = "X" };
Stride.Core.Assets.AssetCollectionItemIdHelper.GenerateMissingItemIds(entity);
entity.Transform.Position = new Stride.Core.Mathematics.Vector3(...);
entity.Components.Add(new Stride.Engine.ModelComponent { Model = modelRef });
var collider = new Stride.Physics.StaticColliderComponent();
collider.ColliderShapes.Add(new Stride.Physics.StaticMeshColliderShapeDesc { Model = modelRef });
entity.Components.Add(collider);
var parts = new Stride.Core.Assets.AssetPartCollection<Stride.Assets.Entities.EntityDesign, Stride.Engine.Entity>
    { new Stride.Assets.Entities.EntityDesign(entity, string.Empty) };
editor.Asset.AssetHierarchyPropertyGraph.AddPartToAsset(
    parts, parts.Single().Value, null, editor.Asset.Asset.Hierarchy.RootParts.Count);
```

Deleting entities by script (no dedicated tool exists): match designs by
entity name inside `editor.Asset.Asset.Hierarchy.Parts`
(`Dictionary<Guid, EntityDesign>`) and call
`editor.Asset.AssetHierarchyPropertyGraph.RemovePartFromAsset(design)` per
entity, inside one transaction. `DeleteParts(IEnumerable<EntityDesign>, ref HashSet)` also exists.

Assigning model assets by script — the design-side value of
`ModelComponent.Model` is a plain `Stride.Rendering.Model` instance carrying
an "attached reference" that the serializer turns into the YAML asset
reference. Build one like this:

```csharp
var modelRef = new Stride.Rendering.Model();
var att = Stride.Core.Serialization.AttachedReferenceManager.GetOrCreateAttachedReference(modelRef);
att.Id = (Stride.Core.Assets.AssetId)guid;   // explicit cast, Guid ≠ AssetId
att.Url = "/nose/Models/Duna_Cresta";        // asset URL as listed by list_assets
```

Verify the pattern first by reading `GetAttachedReference(...)` of an existing
entity's model and comparing Id/Url.

Reflection notes: `AssetId` has no `==` with `Guid` (cast or compare
`ToString()`); `ICommandBase` has parameterless `Execute()` / `CanExecute()`
(the `object` overloads are ambiguous for `GetMethod`).

### Procedural model assets (`*.sdpromodel`) — the reliable way to "create geometry"

Game Studio models like `Ground`/`Sphere` are `ProceduralModelAsset` files on
disk. You can create new ones by writing YAML files into the game project's
`Assets` folder (example `Assets\Models\Duna_Cresta.sdpromodel`):

```yaml
!ProceduralModelAsset
Id: <new unique guid>
SerializedVersion: {Stride: 2.0.0.0}
Tags: []
Type: !CapsuleProceduralModel
    Length: 8.0
    Radius: 3.0
    Tessellation: 24
    UvScale: {X: 6.0, Y: 2.0}
    LocalOffset: {X: 0.0, Y: 0.0, Z: 0.0}
    NumberOfTextureCoordinates: 10
    MaterialInstance:
        Material: 6570c827-43b6-4b5b-b17d-be007fd6ec41:Materials/Desierto de sal
```

- Material reference format: `<assetGuid>:<assetUrl>`. Baking the material
  into the asset avoids touching `ModelComponent.Materials` (an
  `IndexingDictionary` that is awkward to edit by script).
- Available procedural types (all under `Stride.Rendering.ProceduralModels`):
  `PlaneProceduralModel` (Size Vector2, Tessellation Int2), `CapsuleProceduralModel`
  (Length, Radius, Tessellation int), `GeoSphereProceduralModel` /
  `SphereProceduralModel` (Radius, Tessellation), plus Cube, Cylinder, Cone,
  Torus, Teapot. `ProceduralModelDescriptor` is NOT a `Model` and cannot be
  assigned to `ModelComponent.Model` in 4.4.1.
- New files are NOT auto-discovered: trigger a session reload (below), then
  poll `Session.AllAssets` (compare `Id.ToString()`) and use each asset's `Url`.

### Reloading the session by script (discovers new asset files)

```csharp
var dc = global::System.Windows.Application.Current.MainWindow.DataContext; // GameStudioViewModel
var cmd = (Stride.Core.Presentation.Commands.ICommandBase)dc.GetType()
    .GetProperty("ReloadSessionCommand").GetValue(dc);
cmd.Execute(); // parameterless; bridge restarts automatically with the new session
```

The bridge re-binds port 9877 after the reload; wait ~8 s and verify with
`get_project_info` (`assetCount` should increase by the number of new assets).
Do not write new files into `Assets` during other pending work, and ask the
user first if they have unsaved changes (the reload may prompt).

### Transform pitfalls

- `TransformComponent.Scale` applies in **local space BEFORE rotation**.
  To lay a capsule down (ridge), rotate Z -90° and put the cross-section
  scale in local X/Z and the length scale in local Y; a flatten intended for
  world Y must live on the axis that ends up vertical.
- Rotation composition: `Quaternion.Multiply(yawQuat, zRot)` applies Z first,
  then yaw around world Y.
- Bury procedural shapes slightly (`positionY` a bit below 0) so only the
  smooth top shows; fully rounded procedural primitives above the ground look
  like unnatural domes otherwise.
- `Quaternion.RotationYawPitchRoll` takes **radians**; the dedicated
  `set_entity_transform` tool takes degrees.

### What the dedicated tools cannot do (verified gaps)

- No `delete_entity`, no scene save, no game launch, no asset creation.
- `ModelComponent.Materials` is read-only (`IndexingDictionary`); bake
  materials into model assets instead.
- Custom vertex meshes (real heightmap terrain) require a `GraphicsDevice`;
  procedural model assets are the practical alternative.

### Reusable scripts

`Scripts\` contains the battle-tested scripts: `make_desert.ps1.txt`
(create entities from procedural assets), `rebuild_desert.ps1.txt`
(delete + recreate with better layout), `reload_session.ps1.txt`,
and several `introspect_*.ps1.txt` / `probe_*.ps1.txt` API discovery
scripts. Copy and adapt them instead of starting from scratch, and delete
experimental ones when they stop being useful.
