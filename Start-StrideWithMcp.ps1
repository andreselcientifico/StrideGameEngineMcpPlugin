param(
    [Parameter(Mandatory = $true)]
    [string] $GameStudioPath,

    [Parameter(Mandatory = $true)]
    [string] $SolutionPath
)

$ErrorActionPreference = 'Stop'

$gameStudio = (Resolve-Path -LiteralPath $GameStudioPath).Path
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
