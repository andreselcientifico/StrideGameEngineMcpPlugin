$ErrorActionPreference = 'Stop'
$dll = 'C:\Users\Andres\.nuget\packages\stride.core.assets.editor\4.4.1\lib\net10.0-windows7.0\Stride.Core.Assets.Editor.dll'
$text = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($dll))
foreach ($needle in @('OpenedAssets','OpenAssets','SceneEditor','AssetEditorViewModel','DockingDocument','ActiveDocument','OpenDocument','EditorProvider')) {
    if ($text.Contains($needle)) { Write-Output ("FOUND: " + $needle) } else { Write-Output ("no: " + $needle) }
}

$dll2 = 'C:\Users\Andres\.nuget\packages\stride.assets.presentation\4.4.1\lib\net10.0-windows7.0\Stride.Assets.Presentation.dll'
$text2 = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($dll2))
foreach ($needle in @('SceneEditorViewModel','AssetEditorViewModel','OpenAsset','EditorViewModel')) {
    if ($text2.Contains($needle)) { Write-Output ("P2 FOUND: " + $needle) } else { Write-Output ("P2 no: " + $needle) }
}

# List SessionViewModel properties via MetadataLoadContext-free approach: use moniker reflection with deps already loaded in pwsh? Try plain Assembly.LoadFile
try {
    [Reflection.Assembly]::LoadFile($dll) | Out-Null
    $t = $dll.GetType()
} catch {
    Write-Output ("LoadFile note: " + $_.Exception.Message.Split("`n")[0])
}
