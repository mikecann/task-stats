Set-Location "c:\dev\me\mikes-windows-tools"
$msg = "taskmon: polished settings UI, fix ComboBox dropdown z-order, add video to README`n`nSettings dialog: light mode theme, colored metric indicator dots, blue accent`nApply button, section headers with separator lines, icon in title bar.`nFix: pause z-order timer while settings open so ComboBox dropdown stays on top.`nREADME: add vid1.mp4 demo video to taskmon section."
$tmp = [System.IO.Path]::GetTempFileName()
[System.IO.File]::WriteAllText($tmp, $msg)
git add -A
git commit -F $tmp
Remove-Item $tmp
