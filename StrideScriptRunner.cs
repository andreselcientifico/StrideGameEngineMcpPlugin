#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Stride.Core.Assets.Editor.ViewModel;

namespace StrideMcpPlugin.Editor;

internal static class StrideScriptRunner
{
    private const int MaximumScriptLength = 128 * 1024;

    public static bool ConfirmExecution(string script)
    {
        if (script.Length > MaximumScriptLength)
            throw new InvalidDataException($"Scripts are limited to {MaximumScriptLength} characters.");

        return Confirm(
            "Confirm Stride MCP script execution",
            "This C# script runs inside Game Studio with your Windows user permissions. " +
            "It is not sandboxed and can access files, processes, and the network. " +
            "Review the complete script before approving.",
            script);
    }

    public static bool ConfirmOperation(string title, string description, string details)
    {
        return Confirm(title, description, details);
    }

    private static bool Confirm(string title, string description, string details)
    {
        var window = new Window
        {
            Title = title,
            Width = 820,
            Height = 580,
            MinWidth = 520,
            MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.CanResize,
            ShowInTaskbar = false
        };

        Window? owner = Application.Current?.Windows
            .OfType<Window>()
            .FirstOrDefault(candidate => candidate.IsActive)
            ?? Application.Current?.MainWindow;
        if (owner is not null && owner != window)
            window.Owner = owner;

        var layout = new DockPanel { Margin = new Thickness(16) };
        var warning = new TextBlock
        {
            Text = description,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };
        DockPanel.SetDock(warning, Dock.Top);
        layout.Children.Add(warning);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var cancel = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            MinWidth = 90,
            Margin = new Thickness(0, 0, 8, 0)
        };
        var approve = new Button
        {
            Content = "Approve",
            IsDefault = true,
            MinWidth = 100
        };
        approve.Click += (_, _) => window.DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(approve);
        DockPanel.SetDock(buttons, Dock.Bottom);
        layout.Children.Add(buttons);

        layout.Children.Add(new TextBox
        {
            Text = details,
            IsReadOnly = true,
            AcceptsReturn = true,
            AcceptsTab = true,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
        });

        window.Content = layout;
        return window.ShowDialog() == true;
    }

    public static async Task<object> ExecuteAsync(
        SessionViewModel session,
        string source,
        CancellationToken cancellationToken)
    {
        Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => !assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location))
            .GroupBy(assembly => assembly.GetName().Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        ScriptOptions options = ScriptOptions.Default
            .WithReferences(assemblies.Select(assembly => MetadataReference.CreateFromFile(assembly.Location)))
            .WithImports(
                "System",
                "System.IO",
                "System.Linq",
                "System.Threading",
                "System.Threading.Tasks",
                "Stride.Core",
                "Stride.Core.Assets.Editor.ViewModel",
                "Stride.Engine");

        var script = CSharpScript.Create<object?>(
            source,
            options,
            typeof(StrideScriptGlobals));
        var errors = script.Compile(cancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(diagnostic => diagnostic.ToString())
            .Take(50)
            .ToArray();
        if (errors.Length > 0)
            throw new InvalidOperationException("Script compilation failed:" + Environment.NewLine +
                                                string.Join(Environment.NewLine, errors));

        ScriptState<object?> state = await script.RunAsync(
            new StrideScriptGlobals(session),
            cancellationToken: cancellationToken);
        object? value = state.ReturnValue;

        return new
        {
            executed = true,
            resultType = value?.GetType().FullName,
            result = value switch
            {
                null => null,
                string text => text.Length <= 4096 ? text : text[..4096],
                bool or byte or sbyte or short or ushort or int or uint or long or ulong or
                    float or double or decimal or char or Enum => Convert.ToString(value),
                _ => $"Returned {value.GetType().FullName}; use a string or primitive value to return content."
            }
        };
    }
}

public sealed class StrideScriptGlobals(SessionViewModel session)
{
    public SessionViewModel Session { get; } = session;

    public Task<T> OnUiThreadAsync<T>(
        Func<T> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Session.Dispatcher.InvokeAsync(action, cancellationToken);
    }
}
