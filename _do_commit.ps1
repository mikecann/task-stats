Set-Location "c:\dev\me\mikes-windows-tools"
$msg = "taskmon: add run-on-startup support (default on)`n`nAdds RunOnStartup setting (default true) that writes/removes`nHKCU\...\Run\taskmon on every launch and on Apply in Settings.`nBehaviour tab gets a 'Launch at Windows startup' checkbox.`ntaskmon.ps1 passes PSScriptRoot to App.Run() so the VBS path is known.`nbuild.bat adds /r:System.dll for Microsoft.Win32.Registry."
$tmp = [System.IO.Path]::GetTempFileName()
[System.IO.File]::WriteAllText($tmp, $msg)
git add -A
git commit -F $tmp
Remove-Item $tmp
