# task-stats.ps1 -- Taskbar system monitor launcher
#
# The C# source lives in the .cs files.  Build once with build.bat, then this
# script just loads the pre-built DLL and starts the UI.  No compilation here.
#
# First-time setup:
#   1. Open a terminal in this folder
#   2. Run:  build.bat
#   3. After that, task-stats.vbs starts instantly every time
#
# After any code change to any .cs file:
#   1. Kill the running task-stats (right-click overlay > Quit)
#   2. Run build.bat again
#   3. Relaunch via task-stats.vbs

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

# -- Single-instance guard -----------------------------------------------------
$_mtx = New-Object System.Threading.Mutex($false, 'Global\TaskStats_SingleInstance')
if (-not $_mtx.WaitOne(0)) {
    [System.Windows.Forms.MessageBox]::Show(
        'task-stats is already running.  Check the taskbar.', 'task-stats',
        [System.Windows.Forms.MessageBoxButtons]::OK,
        [System.Windows.Forms.MessageBoxIcon]::Information) | Out-Null
    exit
}

# -- Load pre-built DLL --------------------------------------------------------
$dll = Join-Path $env:LOCALAPPDATA 'task-stats\task-stats.dll'

if (-not (Test-Path $dll)) {
    $buildBat = Join-Path $PSScriptRoot 'build.bat'
    [System.Windows.Forms.MessageBox]::Show(
        "task-stats has not been built yet.`n`nPlease run build.bat first:`n`n  $buildBat`n`nThis only needs to be done once (and again after any code changes).",
        'task-stats -- not built',
        [System.Windows.Forms.MessageBoxButtons]::OK,
        [System.Windows.Forms.MessageBoxIcon]::Warning) | Out-Null
    exit
}

[System.Reflection.Assembly]::LoadFrom($dll) | Out-Null
# Pass the script directory so the C# side can locate task-stats.vbs for the startup registry entry.
[TaskMon.App]::Run($PSScriptRoot)
