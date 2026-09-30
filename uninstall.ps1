# Remove only launchers that still belong to this clone. Keep settings, cached
# builds and the shared tools directory/PATH entry for other installed tools.
param([string]$ToolsDir = 'C:\dev\tools')

$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'task-stats uninstalls on Windows only.'
}

$RepoDir = $PSScriptRoot
. (Join-Path $RepoDir 'install-lib.ps1')
$batPath = Join-Path $ToolsDir 'task-stats.bat'
if (Test-Path $batPath) {
    $expected = Get-TaskStatsBatContent $RepoDir
    if ((Get-Content $batPath -Raw).Trim() -ieq $expected.Trim()) {
        Remove-Item $batPath
        $bashPath = Join-Path $ToolsDir 'task-stats'
        if ((Test-Path $bashPath) -and
            (Get-Content $bashPath -Raw).Trim() -ceq (Get-BashStubContent 'task-stats').Trim()) {
            Remove-Item $bashPath
        }
    }
}

$wsh = New-Object -ComObject WScript.Shell
$launcher = Join-Path $RepoDir 'task-stats.vbs'
$startMenuPath = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Task Stats.lnk'
foreach ($path in @((Join-Path $ToolsDir 'Task Stats.lnk'), $startMenuPath)) {
    if (Test-Path $path) {
        $shortcut = $wsh.CreateShortcut($path)
        if ($shortcut.Arguments -ieq "`"$launcher`"" -and
            [System.IO.Path]::GetFileName($shortcut.TargetPath) -ieq 'wscript.exe') {
            Remove-Item $path
        }
    }
}

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if (Test-Path $runKey) {
    $value = Get-ItemPropertyValue -Path $runKey -Name 'task-stats' -ErrorAction SilentlyContinue
    if ($value -ieq "wscript.exe `"$launcher`"") {
        Remove-ItemProperty -Path $runKey -Name 'task-stats'
    }
}

Write-Host 'Removed task-stats launchers for this clone. Settings and cached builds were kept.' -ForegroundColor Green
