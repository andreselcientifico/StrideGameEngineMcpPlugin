#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Stride.Core.Assets.Editor.ViewModel;

namespace StrideMcpPlugin.Editor;

internal sealed record StrideBuildTarget(string ProjectFile, string Configuration);

internal static class StrideBuildRunner
{
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(10);
    private const int MaximumOutputLength = 30000;

    public static StrideBuildTarget GetTarget(SessionViewModel session, string configuration)
    {
        if (!string.Equals(configuration, "Debug", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(configuration, "Release", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Build configuration must be either Debug or Release.");
        }

        var project = session.CurrentProject
            ?? throw new InvalidOperationException("No current Stride project is selected in Game Studio.");
        string projectPath = Path.GetFullPath(project.ProjectPath.ToString());
        string projectDirectory = Path.GetDirectoryName(projectPath)
            ?? throw new InvalidOperationException("Could not determine the current Stride project's directory.");

        string expectedProjectPath = Path.Combine(projectDirectory, $"{project.Name}.csproj");
        if (File.Exists(expectedProjectPath))
            return new StrideBuildTarget(expectedProjectPath, NormalizeConfiguration(configuration));

        string[] projectFiles = Directory.EnumerateFiles(projectDirectory, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                             part.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                             part.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                             part.Equals(".vs", StringComparison.OrdinalIgnoreCase) ||
                             part.Equals("packages", StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        string[] nameMatches = projectFiles
            .Where(path => string.Equals(
                Path.GetFileNameWithoutExtension(path),
                project.Name,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (nameMatches.Length == 1)
            return new StrideBuildTarget(nameMatches[0], NormalizeConfiguration(configuration));

        string[] gameMatches = projectFiles
            .Where(path => Path.GetFileNameWithoutExtension(path)
                .EndsWith(".Game", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (gameMatches.Length == 1)
            return new StrideBuildTarget(gameMatches[0], NormalizeConfiguration(configuration));

        if (projectFiles.Length == 1)
            return new StrideBuildTarget(projectFiles[0], NormalizeConfiguration(configuration));

        string discovered = projectFiles.Length == 0
            ? "No .csproj files were found next to the Stride package."
            : "Multiple .csproj files were found: " + string.Join(", ", projectFiles.Select(Path.GetFileName));
        throw new InvalidOperationException(
            $"{discovered} Open the game project in Game Studio with its matching .csproj beside the .sdpkg file.");
    }

    public static async Task<object> BuildAsync(
        StrideBuildTarget target,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = Path.GetDirectoryName(target.ProjectFile)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(target.ProjectFile);
        startInfo.ArgumentList.Add("--configuration");
        startInfo.ArgumentList.Add(target.Configuration);
        startInfo.ArgumentList.Add("--nologo");

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException("Failed to start dotnet build.");

        Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> standardErrorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(BuildTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            if (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                throw new TimeoutException($"dotnet build exceeded the {BuildTimeout.TotalMinutes:0}-minute timeout.");
            throw;
        }

        string standardOutput = await standardOutputTask;
        string standardError = await standardErrorTask;
        stopwatch.Stop();
        string output = CombineOutput(standardOutput, standardError);

        return new
        {
            success = process.ExitCode == 0,
            exitCode = process.ExitCode,
            configuration = target.Configuration,
            projectFile = target.ProjectFile,
            elapsedMilliseconds = stopwatch.ElapsedMilliseconds,
            output = output.Length <= MaximumOutputLength
                ? output
                : output[^MaximumOutputLength..]
        };
    }

    private static string NormalizeConfiguration(string configuration) =>
        string.Equals(configuration, "Release", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";

    private static string CombineOutput(string standardOutput, string standardError)
    {
        if (standardError.Length == 0)
            return standardOutput;
        if (standardOutput.Length == 0)
            return standardError;
        return standardOutput + Environment.NewLine + standardError;
    }
}
