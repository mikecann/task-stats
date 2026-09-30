# Shared by install.ps1 and uninstall.ps1 so the uninstall ownership checks use
# exactly the same content that the installer writes.
function Get-BashStubContent {
    param([string]$ToolName)
    return @'
#!/usr/bin/env bash
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec "$SCRIPT_DIR/__TOOL_NAME__.bat" "$@"
'@.Replace('__TOOL_NAME__', $ToolName)
}

function Get-TaskStatsBatContent {
    param([string]$RepoDir)
    $launcher = Join-Path $RepoDir 'task-stats.vbs'
    return "@echo off`r`nwscript.exe `"$launcher`""
}

function Write-BatStub {
    param([string]$ToolName, [string]$Content, [string]$ToolsDir)
    $batDest = Join-Path $ToolsDir "$ToolName.bat"
    Set-Content -Path $batDest -Value $Content -Encoding ASCII
    Write-Host "  [bat]  $batDest" -ForegroundColor Green

    $bashDest = Join-Path $ToolsDir $ToolName
    $bashContent = (Get-BashStubContent $ToolName).Replace("`r`n", "`n") + "`n"
    [System.IO.File]::WriteAllText($bashDest, $bashContent, [System.Text.Encoding]::ASCII)
    Write-Host "  [bash] $bashDest" -ForegroundColor Green
}
