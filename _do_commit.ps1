Set-Location "c:\dev\me\mikes-windows-tools"
$msg = "taskmon: upload/download colors + arrows, separate GPU temp color`n`nUpload default red, download default green, arrow glyphs in labels.`nGPU section shows util% and temp in distinct colors side-by-side.`nAdds ColorGpuTemp setting with its own swatch in the Settings Colours tab."
$tmp = [System.IO.Path]::GetTempFileName()
[System.IO.File]::WriteAllText($tmp, $msg)
git add -A
git commit -F $tmp
Remove-Item $tmp
