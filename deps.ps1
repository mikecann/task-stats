# deps.ps1 — dependency check for taskmon
#
# taskmon has no installable dependencies; it only needs nvml.dll which ships
# with NVIDIA drivers.  This script reports the status so the user knows what
# to expect before first launch.

Write-Host "  [taskmon] Checking dependencies..." -ForegroundColor Cyan

$nvml = 'C:\Windows\System32\nvml.dll'
if (Test-Path $nvml) {
    Write-Host "  [taskmon] nvml.dll found — NVIDIA GPU temperature and utilisation available." `
        -ForegroundColor Green
} else {
    Write-Host "  [taskmon] WARN: nvml.dll not found at $nvml" -ForegroundColor Yellow
    Write-Host "            GPU temperature and utilisation will be unavailable." -ForegroundColor DarkGray
    Write-Host "            Install NVIDIA drivers to enable GPU monitoring." -ForegroundColor DarkGray
}

Write-Host "  [taskmon] No other dependencies required." -ForegroundColor Green
