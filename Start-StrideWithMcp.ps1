param(
    [string] $GameStudioPath,

    [string] $SolutionPath
)

$ErrorActionPreference = 'Stop'

function Ask-Path {
    param([string] $Prompt)
    $answer = Read-Host "$Prompt (ruta absoluta)"
    if ([string]::IsNullOrWhiteSpace($answer)) { throw "No path provided; aborting." }
    if (-not (Test-Path -LiteralPath $answer)) { throw "Path does not exist: '$answer'" }
    return (Resolve-Path -LiteralPath $answer).Path
}

# --- Game Studio exe ---
if ([string]::IsNullOrWhiteSpace($GameStudioPath)) {
    $defaultGameStudio = Join-Path $env:USERPROFILE '.nuget\packages\stride.gamestudio\4.4.1\tools\net10.0-windows7.0\Stride.GameStudio.exe'
    if (Test-Path -LiteralPath $defaultGameStudio) {
        $GameStudioPath = $defaultGameStudio
        Write-Host "Game Studio (default): $GameStudioPath"
    }
    else {
        Write-Warning "Game Studio not found at the default location: $defaultGameStudio"
        $GameStudioPath = Ask-Path "Ruta de Stride.GameStudio.exe"
    }
}
if (-not (Test-Path -LiteralPath $GameStudioPath)) {
    Write-Warning "Game Studio not found at: $GameStudioPath"
    $GameStudioPath = Ask-Path "Ruta de Stride.GameStudio.exe"
}
$gameStudio = (Resolve-Path -LiteralPath $GameStudioPath).Path

# --- Solution (.slnx) ---
if ([string]::IsNullOrWhiteSpace($SolutionPath)) {
    # Candidate roots: the user's real Documents folder (wherever moved),
    # its 'Stride Projects' subfolder, and the known-moved location.
    $projectsRoots = [System.Collections.Generic.List[string]]::new()
    $docsFolder = [Environment]::GetFolderPath('MyDocuments')
    if (-not [string]::IsNullOrWhiteSpace($docsFolder)) {
        $projectsRoots.Add($docsFolder.TrimEnd('\') + '\')
        $projectsRoots.Add($docsFolder.TrimEnd('\') + '\Stride Projects\')
    }
    $projectsRoots.Add('D:\Documentos\')
    $projectsRoots.Add('D:\Documentos\Stride Projects\')

    while ([string]::IsNullOrWhiteSpace($SolutionPath)) {
        $solutions = $projectsRoots |
            Where-Object { Test-Path -LiteralPath $_ } |
            ForEach-Object { Get-ChildItem -LiteralPath $_ -Filter '*.slnx' -Recurse -File -Depth 3 -ErrorAction SilentlyContinue -WarningAction SilentlyContinue } |
            Sort-Object FullName -Unique

        if ($solutions.Count -gt 0) {
            Write-Host ""
            Write-Host "Soluiones encontradas:"
            for ($i = 0; $i -lt $solutions.Count; $i++) {
                Write-Host ("  [{0}] {1}" -f ($i + 1), $solutions[$i].FullName)
            }
            Write-Host "  [r] Buscar en otra carpeta"
            Write-Host "  [x] Usar una ruta externa manualmente"
            $choice = Read-Host "Elige una opcion (default 1)"
            if ([string]::IsNullOrWhiteSpace($choice) -or $choice -eq '1') {
                $SolutionPath = $solutions[0].FullName
            }
            elseif ($choice -match '^[1-9]\d?$' -and [int]$choice -le $solutions.Count) {
                $SolutionPath = $solutions[[int]$choice - 1].FullName
            }
            elseif ($choice -eq 'r') {
                $newRoot = Read-Host "Raiz donde buscar proyectos"
                if (Test-Path -LiteralPath $newRoot) {
                    $projectsRoots.Insert(0, ((Resolve-Path -LiteralPath $newRoot).Path.TrimEnd('\') + '\'))
                }
                else {
                    Write-Warning "Ruta no valida: '$newRoot'"
                }
            }
            else {
                try { $SolutionPath = Ask-Path "Ruta del archivo .slnx" } catch { Write-Warning $_.Exception.Message }
            }
        }
        else {
            Write-Warning "No se encontraron .slnx en la carpeta Documentos."
            Write-Host "  [r] Buscar en otra carpeta"
            Write-Host "  [x] Usar una ruta externa manualmente"
            $choice = Read-Host "Elige una opcion (default r)"
            if ($choice -eq 'x') {
                try { $SolutionPath = Ask-Path "Ruta del archivo .slnx" } catch { Write-Warning $_.Exception.Message }
            }
            else {
                $newRoot = Read-Host "Raiz donde buscar proyectos"
                if (Test-Path -LiteralPath $newRoot) {
                    $projectsRoots.Insert(0, ((Resolve-Path -LiteralPath $newRoot).Path.TrimEnd('\') + '\'))
                }
                else {
                    Write-Warning "Ruta no valida: '$newRoot'"
                }
            }
        }
    }
}
if (-not (Test-Path -LiteralPath $SolutionPath)) {
    Write-Warning "Solucion no encontrada en: $SolutionPath"
    $SolutionPath = Ask-Path "Ruta del archivo .slnx"
}
$solution = (Resolve-Path -LiteralPath $SolutionPath).Path

$pluginProject = Join-Path $PSScriptRoot 'StrideMcpPlugin.Editor.csproj'
$bootstrapProject = Join-Path $PSScriptRoot 'StrideMcpBootstrap\StrideMcpBootstrap.csproj'
$pluginOutput = Join-Path $PSScriptRoot 'bin\Debug\net10.0-windows-opencode-entities\'
$bootstrapOutput = Join-Path $PSScriptRoot 'StrideMcpBootstrap\bin\Debug\net10.0-opencode-msbuild-fix\'
$pluginAssembly = Join-Path $pluginOutput 'StrideMcpPlugin.Editor.dll'
$startupHook = Join-Path $bootstrapOutput 'StrideMcpBootstrap.dll'

& dotnet build $pluginProject "-p:OutDir=$pluginOutput" 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "Build failed for '$pluginProject'."
}

& dotnet build $bootstrapProject "-p:OutDir=$bootstrapOutput" 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "Build failed for '$bootstrapProject'."
}

$previousStartupHooks = $env:DOTNET_STARTUP_HOOKS
$previousPluginPath = $env:STRIDE_MCP_PLUGIN_PATH

try {
    if ([string]::IsNullOrWhiteSpace($previousStartupHooks)) {
        $env:DOTNET_STARTUP_HOOKS = $startupHook
    }
    else {
        $env:DOTNET_STARTUP_HOOKS = "$startupHook;$previousStartupHooks"
    }

    $env:STRIDE_MCP_PLUGIN_PATH = $pluginAssembly
    & $gameStudio $solution
    $exitCode = $LASTEXITCODE
}
finally {
    $env:DOTNET_STARTUP_HOOKS = $previousStartupHooks
    $env:STRIDE_MCP_PLUGIN_PATH = $previousPluginPath
}

exit $exitCode
