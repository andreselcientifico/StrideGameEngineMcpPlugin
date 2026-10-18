#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using Stride.Core.Assets;
using Stride.Core.Assets.Editor.ViewModel;
using Stride.Core.Mathematics;
using Stride.Assets.Entities;
using Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.ViewModels;
using Stride.Assets.Presentation.AssetEditors.SceneEditor.ViewModels;
using Stride.Engine;

namespace StrideMcpPlugin.Editor;

internal static class StrideSceneOperations
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static object ListEntities()
    {
        SceneEditorViewModel editor = GetOpenSceneEditor();
        EntityViewModel[] entities = EnumerateEntities(editor.HierarchyRoot).ToArray();
        return new
        {
            scene = editor.Asset.Name,
            sceneUrl = editor.Asset.Url,
            count = entities.Length,
            entities = entities.Select(entity => new
            {
                id = entity.AssetSideEntity.Id.ToString(),
                entity.Name,
                parent = (entity.TransformParent as EntityViewModel)?.Name,
                position = Vector(entity.AssetSideEntity.Transform.Position),
                rotation = QuaternionValue(entity.AssetSideEntity.Transform.Rotation),
                scale = Vector(entity.AssetSideEntity.Transform.Scale),
                components = entity.Components.Select(component => component.GetType().FullName).ToArray()
            }).ToArray()
        };
    }

    public static object ListComponents(string entityName)
    {
        EntityViewModel entity = FindEntity(GetOpenSceneEditor(), entityName);
        var components = entity.Components.Select(component => new
        {
            type = component.GetType().FullName,
            properties = component.GetType()
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(property => property.GetIndexParameters().Length == 0 && property.GetMethod is not null)
                .Select(property => new
                {
                    property.Name,
                    type = property.PropertyType.FullName,
                    writable = property.SetMethod?.IsPublic == true
                })
                .ToArray()
        }).ToArray();

        return new { entity = entity.Name, count = components.Length, components };
    }

    public static object CreateEntity(string name, string? parentName)
    {
        SceneEditorViewModel editor = GetOpenSceneEditor();
        ValidateName(name, nameof(name));
        if (EnumerateEntities(editor.HierarchyRoot).Any(entity =>
                string.Equals(entity.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"An entity named '{name}' already exists in this scene.");

        EntityViewModel? parent = string.IsNullOrWhiteSpace(parentName)
            ? null
            : FindEntity(editor, parentName);
        var entity = new Entity { Name = name };
        AssetCollectionItemIdHelper.GenerateMissingItemIds(entity);

        var parts = new AssetPartCollection<EntityDesign, Entity>
        {
            new EntityDesign(entity, string.Empty)
        };

        using (var transaction = editor.UndoRedoService.CreateTransaction())
        {
            editor.Asset.AssetHierarchyPropertyGraph.AddPartToAsset(
                parts,
                parts.Single().Value,
                parent?.AssetSideEntity,
                parent is null
                    ? editor.Asset.Asset.Hierarchy.RootParts.Count
                    : parent.AssetSideEntity.Transform.Children.Count);
            editor.UndoRedoService.SetName(transaction, $"Create entity '{name}'");
        }

        return new { created = true, id = entity.Id.ToString(), entity = name, parent = parent?.Name };
    }

    public static object RenameEntity(string entityName, string newName)
    {
        SceneEditorViewModel editor = GetOpenSceneEditor();
        ValidateName(newName, nameof(newName));
        if (EnumerateEntities(editor.HierarchyRoot).Any(entity =>
                !string.Equals(entity.Name, entityName, StringComparison.Ordinal) &&
                string.Equals(entity.Name, newName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"An entity named '{newName}' already exists in this scene.");

        EntityViewModel entity = FindEntity(editor, entityName);
        using (var transaction = editor.UndoRedoService.CreateTransaction())
        {
            entity.Name = newName;
            editor.UndoRedoService.SetName(transaction, $"Rename entity '{entityName}'");
        }

        return new { renamed = true, previousName = entityName, entity = entity.Name };
    }

    public static object SetTransform(
        string entityName,
        float? positionX,
        float? positionY,
        float? positionZ,
        float? rotationX,
        float? rotationY,
        float? rotationZ,
        float? scaleX,
        float? scaleY,
        float? scaleZ)
    {
        EntityHierarchyEditorViewModel editor = GetOpenSceneEditor();
        EntityViewModel entity = FindEntity(editor, entityName);
        if (!positionX.HasValue && !positionY.HasValue && !positionZ.HasValue &&
            !rotationX.HasValue && !rotationY.HasValue && !rotationZ.HasValue &&
            !scaleX.HasValue && !scaleY.HasValue && !scaleZ.HasValue)
            throw new InvalidDataException("Specify at least one position, rotation, or scale value.");

        TransformComponent transform = entity.AssetSideEntity.Transform;
        Vector3 position = new(
            positionX ?? transform.Position.X,
            positionY ?? transform.Position.Y,
            positionZ ?? transform.Position.Z);
        Vector3 scale = new(
            scaleX ?? transform.Scale.X,
            scaleY ?? transform.Scale.Y,
            scaleZ ?? transform.Scale.Z);
        Quaternion rotation = rotationX.HasValue || rotationY.HasValue || rotationZ.HasValue
            ? Quaternion.RotationYawPitchRoll(
                (rotationY ?? 0) * MathF.PI / 180f,
                (rotationX ?? 0) * MathF.PI / 180f,
                (rotationZ ?? 0) * MathF.PI / 180f)
            : transform.Rotation;

        using (var transaction = editor.UndoRedoService.CreateTransaction())
        {
            var nodeContainer = GetNodeContainer(editor);
            var node = nodeContainer.GetNode(transform)
                ?? throw new InvalidOperationException("Stride could not create a property node for the transform.");
            if (transform.Position != position)
                UpdateNodeProperty(node, nameof(TransformComponent.Position), position);
            if (transform.Rotation != rotation)
                UpdateNodeProperty(node, nameof(TransformComponent.Rotation), rotation);
            if (transform.Scale != scale)
                UpdateNodeProperty(node, nameof(TransformComponent.Scale), scale);
            editor.UndoRedoService.SetName(transaction, $"Update transform for '{entityName}'");
        }

        return new
        {
            entity = entity.Name,
            position = Vector(position),
            rotation = QuaternionValue(rotation),
            scale = Vector(scale)
        };
    }

    public static object AddComponent(string entityName, string componentTypeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(componentTypeName);
        EntityHierarchyEditorViewModel editor = GetOpenSceneEditor();
        EntityViewModel entity = FindEntity(editor, entityName);
        Type componentType = ResolveComponentType(componentTypeName);

        if (entity.Components.Any(existing => existing.GetType() == componentType))
            throw new InvalidOperationException($"Entity '{entityName}' already has a {componentType.FullName} component.");

        if (Activator.CreateInstance(componentType) is not EntityComponent component)
            throw new InvalidOperationException(
                $"Component '{componentType.FullName}' needs a public parameterless constructor.");

        using (var transaction = editor.UndoRedoService.CreateTransaction())
        {
            entity.AssetSideEntity.Components.Add(component);
            AssetCollectionItemIdHelper.GenerateMissingItemIds(entity.AssetSideEntity);
            editor.UndoRedoService.SetName(transaction, $"Add {componentType.Name} to '{entityName}'");
        }

        return new
        {
            added = true,
            entity = entity.Name,
            component = componentType.FullName
        };
    }

    public static object SetComponentProperty(
        string entityName,
        string componentTypeName,
        string propertyName,
        JsonElement value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        EntityHierarchyEditorViewModel editor = GetOpenSceneEditor();
        EntityViewModel entity = FindEntity(editor, entityName);
        EntityComponent component = FindComponent(entity, componentTypeName);
        PropertyInfo property = component.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException(
                $"Public property '{propertyName}' was not found on {component.GetType().FullName}.");
        if (property.SetMethod?.IsPublic != true || property.GetIndexParameters().Length != 0)
            throw new InvalidOperationException($"Property '{propertyName}' is not publicly writable.");

        object convertedValue = ConvertValue(value, property.PropertyType);
        using (var transaction = editor.UndoRedoService.CreateTransaction())
        {
            var node = GetNodeContainer(editor).GetNode(component)
                ?? throw new InvalidOperationException(
                    $"Stride could not create a property node for {component.GetType().FullName}.");
            UpdateNodeProperty(node, propertyName, convertedValue);
            editor.UndoRedoService.SetName(transaction, $"Set {componentTypeName}.{propertyName} on '{entityName}'");
        }

        return new
        {
            updated = true,
            entity = entity.Name,
            component = component.GetType().FullName,
            property = property.Name,
            value = value.Clone()
        };
    }

    public static object RemoveComponent(string entityName, string componentTypeName)
    {
        EntityHierarchyEditorViewModel editor = GetOpenSceneEditor();
        EntityViewModel entity = FindEntity(editor, entityName);
        EntityComponent component = FindComponent(entity, componentTypeName);
        if (component is TransformComponent)
            throw new InvalidOperationException("The TransformComponent is required and cannot be removed.");

        using (var transaction = editor.UndoRedoService.CreateTransaction())
        {
            if (!entity.AssetSideEntity.Components.Remove(component))
                throw new InvalidOperationException($"Component '{component.GetType().FullName}' could not be removed.");
            editor.UndoRedoService.SetName(transaction, $"Remove {component.GetType().Name} from '{entityName}'");
        }

        return new { removed = true, entity = entity.Name, component = component.GetType().FullName };
    }

    private static SceneEditorViewModel GetOpenSceneEditor()
    {
        Application application = Application.Current
            ?? throw new InvalidOperationException("Game Studio's WPF application is unavailable.");

        SceneEditorViewModel[] activeWindowEditors = application.Windows
            .OfType<Window>()
            .Where(window => window.IsActive)
            .SelectMany(GetLogicalTree)
            .OfType<SceneEditorViewModel>()
            .Distinct()
            .ToArray();
        if (activeWindowEditors.Length == 1)
            return activeWindowEditors[0];
        if (activeWindowEditors.Length > 1)
            throw new InvalidOperationException("More than one Stride scene editor is active. Focus a single scene editor and retry.");

        SceneEditorViewModel[] editors = application.Windows
            .OfType<Window>()
            .SelectMany(GetLogicalTree)
            .OfType<SceneEditorViewModel>()
            .Distinct()
            .ToArray();
        return editors.Length switch
        {
            1 => editors[0],
            0 => throw new InvalidOperationException(
                "No open scene editor was found. Open a scene asset in Game Studio and focus its scene editor."),
            _ => throw new InvalidOperationException(
                "Multiple scene editors are open. Focus the scene you want to edit and retry.")
        };
    }

    private static IEnumerable<object> GetLogicalTree(Window root)
    {
        var pending = new Stack<object>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        pending.Push(root);

        while (pending.Count > 0)
        {
            object current = pending.Pop();
            if (!visited.Add(current))
                continue;

            yield return current;
            if (current is FrameworkElement element && element.DataContext is not null)
                pending.Push(element.DataContext);

            if (current is DependencyObject dependencyObject)
            {
                foreach (object child in LogicalTreeHelper.GetChildren(dependencyObject).OfType<object>())
                    pending.Push(child);
            }

            // Dockable document content (asset editors) comes from data
            // templates and exists only in the visual tree; the logical-tree
            // walk above never reaches it. Walk the visual tree as well.
            if (current is System.Windows.Media.Visual)
            {
                DependencyObject visualObject = (DependencyObject)current;
                int childCount = System.Windows.Media.VisualTreeHelper.GetChildrenCount(visualObject);
                for (int index = 0; index < childCount; index++)
                    pending.Push(System.Windows.Media.VisualTreeHelper.GetChild(visualObject, index));
            }
        }
    }

    private static IEnumerable<EntityViewModel> EnumerateEntities(EntityHierarchyItemViewModel parent)
    {
        foreach (EntityViewModel child in parent.TransformChildren)
        {
            yield return child;
            foreach (EntityViewModel descendant in EnumerateEntities(child))
                yield return descendant;
        }
    }

    private static Stride.Core.Quantum.NodeContainer GetNodeContainer(EntityHierarchyEditorViewModel editor)
    {
        SessionViewModel session = editor.Session
            ?? throw new InvalidOperationException("The scene editor is no longer attached to a session.");
        return session.AssetNodeContainer
            ?? throw new InvalidOperationException("Stride's asset node container is unavailable.");
    }

    private static void UpdateNodeProperty(Stride.Core.Quantum.IObjectNode node, string propertyName, object value)
    {
        var memberNode = node[propertyName]
            ?? throw new InvalidOperationException($"Stride's property node '{propertyName}' is unavailable.");
        memberNode.Update(value);
    }

    private static EntityViewModel FindEntity(EntityHierarchyEditorViewModel editor, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        EntityViewModel[] matches = EnumerateEntities(editor.HierarchyRoot)
            .Where(entity => string.Equals(entity.Name, name, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException($"No entity named '{name}' exists in the active scene."),
            _ => throw new InvalidOperationException(
                $"More than one entity is named '{name}'. Rename the duplicates so the target is unambiguous.")
        };
    }

    private static EntityComponent FindComponent(EntityViewModel entity, string typeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        EntityComponent[] matches = entity.Components
            .Where(component =>
                string.Equals(component.GetType().FullName, typeName, StringComparison.Ordinal) ||
                string.Equals(component.GetType().Name, typeName, StringComparison.Ordinal))
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException($"Entity '{entity.Name}' has no component matching '{typeName}'."),
            _ => throw new InvalidOperationException(
                $"Entity '{entity.Name}' has multiple components matching '{typeName}'. Use the full type name.")
        };
    }

    private static Type ResolveComponentType(string typeName)
    {
        Type[] matches = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => !assembly.IsDynamic)
            .SelectMany(assembly =>
            {
                try
                {
                    return assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException exception)
                {
                    return exception.Types.OfType<Type>();
                }
            })
            .Where(type =>
                typeof(EntityComponent).IsAssignableFrom(type) &&
                (string.Equals(type.FullName, typeName, StringComparison.Ordinal) ||
                 string.Equals(type.Name, typeName, StringComparison.Ordinal)))
            .Distinct()
            .ToArray();
        return matches.Length switch
        {
            1 when !matches[0].IsAbstract && !matches[0].ContainsGenericParameters => matches[0],
            1 => throw new InvalidOperationException($"Component type '{typeName}' is abstract or generic."),
            0 => throw new InvalidOperationException(
                $"No loaded Stride EntityComponent type named '{typeName}' was found."),
            _ => throw new InvalidOperationException(
                $"Component name '{typeName}' is ambiguous. Specify its fully-qualified type name.")
        };
    }

    private static object ConvertValue(JsonElement value, Type targetType)
    {
        Type effectiveType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        try
        {
            if (effectiveType == typeof(Vector3))
                return new Vector3(
                    ReadFloat(value, "x", 0),
                    ReadFloat(value, "y", 1),
                    ReadFloat(value, "z", 2));
            if (effectiveType == typeof(Quaternion))
                return new Quaternion(
                    ReadFloat(value, "x", 0),
                    ReadFloat(value, "y", 1),
                    ReadFloat(value, "z", 2),
                    ReadFloat(value, "w", 3));
            if (effectiveType.IsEnum)
            {
                return value.ValueKind == JsonValueKind.String
                    ? Enum.Parse(effectiveType, value.GetString()!, ignoreCase: true)
                    : Enum.ToObject(effectiveType, value.GetInt32());
            }

            return JsonSerializer.Deserialize(value.GetRawText(), targetType, JsonOptions)
                ?? throw new InvalidDataException($"Cannot assign null to '{targetType.FullName}'.");
        }
        catch (Exception exception) when (
            exception is JsonException or FormatException or InvalidOperationException or OverflowException)
        {
            throw new InvalidDataException(
                $"Value {value.GetRawText()} cannot be converted to '{targetType.FullName}'. " +
                "Stride Vector3 values use {\"x\": number, \"y\": number, \"z\": number}; quaternions also require \"w\".",
                exception);
        }
    }

    private static float ReadFloat(JsonElement element, string propertyName, int arrayIndex)
    {
        JsonElement value;
        if (element.ValueKind == JsonValueKind.Array)
        {
            if (element.GetArrayLength() <= arrayIndex)
                throw new InvalidDataException($"A vector value must have at least {arrayIndex + 1} elements.");
            value = element[arrayIndex];
        }
        else if (element.ValueKind == JsonValueKind.Object &&
                 element.TryGetProperty(propertyName, out JsonElement namedValue))
        {
            value = namedValue;
        }
        else
        {
            throw new InvalidDataException($"A vector value needs a '{propertyName}' property or array element.");
        }

        if (!value.TryGetSingle(out float result) || !float.IsFinite(result))
            throw new InvalidDataException($"Vector coordinate '{propertyName}' must be a finite number.");
        return result;
    }

    private static object Vector(Vector3 value) => new { x = value.X, y = value.Y, z = value.Z };

    private static object QuaternionValue(Quaternion value) =>
        new { x = value.X, y = value.Y, z = value.Z, w = value.W };

    private static void ValidateName(string name, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, parameterName);
        if (name.Length > 128 || name.Any(char.IsControl))
            throw new InvalidDataException("Entity names must be 1-128 characters and cannot contain control characters.");
    }
}
