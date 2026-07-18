# End-to-end test: OpenStrate pipeline manifest <-> Revit bridge <-> MCP proxy.
# Assumes Revit is running with the scan ingested (run dev-loop.ps1 first if not).
param(
    [string]$BridgeUrl = "http://127.0.0.1:8090",
    [string]$PipelineUrl = "http://192.168.7.182:8012"
)
$ErrorActionPreference = "Stop"
$script:pass = 0; $script:fail = 0

function Assert([string]$Name, [bool]$Cond, [string]$Detail = "") {
    if ($Cond) { $script:pass++; Write-Host "  PASS  $Name $Detail" -ForegroundColor Green }
    else       { $script:fail++; Write-Host "  FAIL  $Name $Detail" -ForegroundColor Red }
}
function Invoke-Tool([string]$Tool, $ToolArgs = @{}) {
    $body = @{ tool = $Tool; args = $ToolArgs } | ConvertTo-Json -Depth 5
    (Invoke-RestMethod "$BridgeUrl/call" -Method Post -Body $body -ContentType "application/json" -TimeoutSec 120).result
}

Write-Host "[1] Pipeline manifest (source of truth)" -ForegroundColor Cyan
$manifest = Invoke-RestMethod "$PipelineUrl/ifc.json" -TimeoutSec 15
$mWalls = $manifest.walls_edit.Count
$mObjects = $manifest.edit_objects.Count
Assert "manifest reachable" ($null -ne $manifest.classes) "($mWalls walls, $mObjects objects)"

Write-Host "[2] Bridge server inside Revit" -ForegroundColor Cyan
$tools = Invoke-RestMethod "$BridgeUrl/tools" -TimeoutSec 10
Assert "tool catalog" ($tools.Count -eq 5) "($($tools.Count) tools)"

$stats = Invoke-Tool "model_stats"
Assert "census reads scan doc" ($stats.document -like "scan_*") "($($stats.document))"
Assert "walls match manifest" ($stats.by_category.Walls -eq $mWalls) "(revit=$($stats.by_category.Walls) manifest=$mWalls)"
$furniture = $stats.by_category.Furniture
Assert "furniture present" ($furniture -ge 1) "($furniture)"
Assert "walls+furniture = edit_objects" (($stats.by_category.Walls + $furniture) -eq $mObjects) "($($stats.by_category.Walls)+$furniture vs $mObjects)"

Write-Host "[3] Element queries" -ForegroundColor Cyan
$walls = Invoke-Tool "query_elements" @{ category = "Walls"; limit = 100 }
Assert "query returns all walls" ($walls.Count -eq $mWalls) "($($walls.Count))"
$el = Invoke-Tool "get_element" @{ unique_id = $walls[0].unique_id }
Assert "element dump has parameters" ($el.parameters.PSObject.Properties.Name.Count -gt 0) "($($el.parameters.PSObject.Properties.Name.Count) params)"
Assert "identity is UniqueId" ($el.unique_id -eq $walls[0].unique_id)

Write-Host "[4] Known findings still detected" -ForegroundColor Cyan
Assert "IfcCovering demotion visible" ($stats.by_category.'Generic Models' -ge 1) "(Generic Models=$($stats.by_category.'Generic Models'))"

Write-Host "[5] MCP stdio proxy" -ForegroundColor Cyan
$mcpInput = @(
    '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"e2e","version":"0"}}}'
    '{"jsonrpc":"2.0","method":"notifications/initialized"}'
    '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"model_stats","arguments":{}}}'
) -join "`n"
$mcpOut = ($mcpInput | node "$PSScriptRoot\mcp-proxy\server.mjs" 2>$null) -join "`n"
# The MCP result embeds the payload as an ESCAPED JSON string: \"Walls\": 14
Assert "MCP call round-trips" ($mcpOut -match ('Walls\\?["\\]*:\s*' + $mWalls)) "(walls=$mWalls via MCP)"

Write-Host ""
Write-Host ("E2E RESULT: {0} passed, {1} failed" -f $script:pass, $script:fail) -ForegroundColor $(if ($script:fail -eq 0) { "Green" } else { "Red" })
exit $(if ($script:fail -eq 0) { 0 } else { 1 })
