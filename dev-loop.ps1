# OpenStrate Revit Bridge - full dev loop, no UI clicking.
# Kill Revit -> build+deploy -> launch Revit -> wait for bridge -> open host -> ingest -> stats.
param(
    [string]$RevitExe = "C:\Program Files\Autodesk\Revit 2027\Revit.exe",
    [int]$Port = 8090,
    [int]$StartupTimeoutSec = 300
)
$ErrorActionPreference = "Stop"
$env:Path = [Environment]::GetEnvironmentVariable("Path","Machine") + ";" + [Environment]::GetEnvironmentVariable("Path","User")

Write-Host "[1/5] Closing Revit..." -ForegroundColor Cyan
Get-Process Revit -ErrorAction SilentlyContinue | Stop-Process -Force
while (Get-Process Revit -ErrorAction SilentlyContinue) { Start-Sleep 1 }

Write-Host "[2/5] Build + deploy..." -ForegroundColor Cyan
Push-Location $PSScriptRoot
dotnet build -c Release | Select-String -Pattern "Build succeeded|error" | ForEach-Object { $_.Line.Trim() }
if ($LASTEXITCODE -ne 0) { Pop-Location; throw "Build failed" }
Pop-Location

Write-Host "[3/5] Launching Revit..." -ForegroundColor Cyan
Start-Process $RevitExe
$deadline = (Get-Date).AddSeconds($StartupTimeoutSec)
$up = $false
while ((Get-Date) -lt $deadline) {
    try {
        Invoke-RestMethod "http://127.0.0.1:$Port/tools" -TimeoutSec 2 | Out-Null
        $up = $true; break
    } catch { Start-Sleep 3 }
}
if (-not $up) { throw "Bridge server did not come up within $StartupTimeoutSec s (add-in load prompt? click 'Always Load')." }
Write-Host "    Bridge is up." -ForegroundColor Green

function Invoke-Tool([string]$Tool, $Args = @{}) {
    $body = @{ tool = $Tool; args = $Args } | ConvertTo-Json -Depth 5
    Invoke-RestMethod "http://127.0.0.1:$Port/call" -Method Post -Body $body -ContentType "application/json" -TimeoutSec 300
}

Write-Host "[4/5] open_host + ingest..." -ForegroundColor Cyan
(Invoke-Tool "open_host").result | ConvertTo-Json -Depth 4
(Invoke-Tool "ingest").result | ConvertTo-Json -Depth 4

Write-Host "[5/5] model_stats..." -ForegroundColor Cyan
(Invoke-Tool "model_stats").result | ConvertTo-Json -Depth 6
Write-Host "Done." -ForegroundColor Green
