param(
    [ValidateSet('Open', 'Build', 'Preview', 'Compare', 'Stop')]
    [string]$Mode = 'Open',
    [string]$UnityPath = 'C:\Program Files\Unity\Hub\Editor\6000.6.5f1\Editor\Unity.exe'
)
$ErrorActionPreference = 'Stop'
$projectPath = Split-Path -Parent $PSScriptRoot
$commandPath = Join-Path (Split-Path -Parent $projectPath) 'particle-smoke-command.txt'
$commands = @{ Open = 'fusion-open'; Build = 'fusion-build'; Preview = 'fusion-preview'; Compare = 'fusion-run'; Stop = 'stop' }
$methods = @{ Open = 'SmokeFusionExperiment.Open'; Build = 'SmokeFusionExperiment.Build'; Preview = 'SmokeFusionExperiment.Preview'; Compare = 'SmokeFusionExperiment.Run' }
$editor = Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" | Where-Object {
    $_.CommandLine -and $_.CommandLine.Contains($projectPath) -and $_.CommandLine -notmatch '-assetImportWorker'
}
if ($editor) {
    Set-Content -LiteralPath $commandPath -Value $commands[$Mode] -Encoding UTF8
    Write-Host "Sent $Mode to this project's Unity editor. Unsaved scenes and Play Mode block scene changes; check the Unity Console."
    return
}
if ($Mode -eq 'Stop') { Write-Host 'This project has no running editor.'; return }
if (-not (Test-Path -LiteralPath $UnityPath)) { throw "Unity was not found: $UnityPath" }
$logPath = Join-Path (Split-Path -Parent $projectPath) 'fusion-user-unity.log'
$arguments = @('-projectPath', ('"' + $projectPath + '"'), '-executeMethod', $methods[$Mode], '-logFile', ('"' + $logPath + '"'))
# Opening Unity is an explicit user action through this script. It runs with graphics.
Start-Process -FilePath $UnityPath -ArgumentList $arguments
Write-Host "Opening Unity: $Mode. Log: $logPath"
