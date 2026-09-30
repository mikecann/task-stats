# Install task-stats from this clone. No API key is needed for the overlay.
param(
    [switch]$SkipDeps,
    [string]$ToolsDir = 'C:\dev\tools'
)

$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'task-stats installs on Windows only.'
}

$RepoDir = $PSScriptRoot
. (Join-Path $RepoDir 'install-lib.ps1')

# The command stub is ASCII, like the original installer. Fail instead of
# silently replacing a non-ASCII character in its launch path with a question mark.
if ($RepoDir -match '[^\x00-\x7F]') {
    throw 'Clone task-stats into an ASCII-only path so the batch launcher can find it.'
}

if (-not $SkipDeps) {
    # Run dependency checks in a child process so its exit code is unambiguous.
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $RepoDir 'deps.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'task-stats dependency check/build failed.' }
}

New-Item -ItemType Directory -Path $ToolsDir -Force | Out-Null
Write-BatStub 'task-stats' (Get-TaskStatsBatContent $RepoDir) $ToolsDir

$wsh = New-Object -ComObject WScript.Shell
$launcher = Join-Path $RepoDir 'task-stats.vbs'
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
New-Item -ItemType Directory -Path $startMenu -Force | Out-Null
foreach ($path in @((Join-Path $ToolsDir 'Task Stats.lnk'), (Join-Path $startMenu 'Task Stats.lnk'))) {
    $shortcut = $wsh.CreateShortcut($path)
    $shortcut.TargetPath = 'wscript.exe'
    $shortcut.Arguments = "`"$launcher`""
    $shortcut.WorkingDirectory = $RepoDir
    $shortcut.Description = 'Taskbar system stats: NET / CPU / GPU / MEM sparklines'
    $shortcut.IconLocation = '%SystemRoot%\System32\imageres.dll,174'
    $shortcut.Save()
    Write-Host "  [lnk]  $path" -ForegroundColor Green
}

# Keep the shared directory on PATH. Other standalone tools use it too.
$machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
$onPath = (($machinePath -split ';') + ($userPath -split ';')) |
    Where-Object { $_ -and $_.TrimEnd('\') -ieq $ToolsDir.TrimEnd('\') }
if (-not $onPath) {
    Write-Host "'$ToolsDir' is not on PATH." -ForegroundColor Yellow
    $answer = Read-Host 'Add it to your user PATH now? [Y/n]'
    if ($answer -eq '' -or $answer -imatch '^y') {
        $newUserPath = (([string]$userPath).TrimEnd(';') + ";$ToolsDir").TrimStart(';')
        [Environment]::SetEnvironmentVariable('Path', $newUserPath, 'User')
        $env:PATH += ";$ToolsDir"
    }
}

# This tool had no Explorer verbs or converted icons in the original installer.
Write-Host 'Installed task-stats. Open Task Stats from the Start Menu, or run task-stats in a new terminal.' -ForegroundColor Green
