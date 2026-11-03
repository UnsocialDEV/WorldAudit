param(
    [string]$GameDir = (Join-Path $env:APPDATA 'Vintagestory'),
    [int]$Port = 42429
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$serverExe = Join-Path $GameDir 'VintagestoryServer.exe'
$package = Join-Path $repo 'WorldAudit/bin/Release/net10.0/WorldAudit-vintagestory.zip'
$driver = Join-Path $PSScriptRoot 'bin/Release/net10.0/WorldAudit.ServerTests.dll'
foreach ($required in @($serverExe, $package, $driver)) {
    if (!(Test-Path -LiteralPath $required)) { throw "Missing $required. Build the Release mod package and server test project first." }
}
$run = Join-Path $repo ('artifacts/live-1.22.7/run-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$data = Join-Path $run 'data'
New-Item -ItemType Directory -Path $data -Force | Out-Null
& $serverExe --dataPath $data --genconfig *> (Join-Path $run 'genconfig.log')
if ($LASTEXITCODE -ne 0) { throw 'Server configuration generation failed.' }
$configPath = Join-Path $data 'serverconfig.json'
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
$config.ServerName = 'WorldAudit disposable 1.22.7 integration tests'
$config.Ip = '127.0.0.1'
$config.Port = $Port
$config.AdvertiseServer = $false
$config.Upnp = $false
$config.VerifyPlayerAuth = $false
$config.MaxChunkRadius = 4
$config.PassTimeWhenEmpty = $true
$config.ModPaths = @((Join-Path $GameDir 'Mods'), (Join-Path $data 'Mods'))
$config.MapSizeX = 1024
$config.MapSizeY = 128
$config.MapSizeZ = 1024
$config.WorldConfig.WorldName = 'WorldAudit disposable integration'
$config.WorldConfig.Seed = 'worldaudit1227'
$config.WorldConfig.PlayStyle = 'creativebuilding'
$config.WorldConfig.WorldType = 'superflat'
$config | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $configPath -Encoding utf8
$modConfig = Join-Path $data 'ModConfig'
$testMods = Join-Path $data 'Mods/WorldAudit.ServerTests'
New-Item -ItemType Directory -Path $modConfig, $testMods -Force | Out-Null
@{
    EnableDiscordTelemetry = $false
    DiscordWebhookUrl = ''
    DiscordChannelId = ''
    IncludeOnlinePlayerNamesInTelemetry = $false
    EnableWorldgenCauseProvider = $false
    DatabasePath = (Join-Path $data 'WorldAudit/live-audit.db')
    VerboseDiagnostics = $true
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $modConfig 'worldaudit.json') -Encoding utf8
Copy-Item -LiteralPath $package -Destination (Join-Path $data 'Mods/WorldAudit.zip')
Copy-Item -LiteralPath $driver -Destination $testMods
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'modinfo.json') -Destination $testMods
Set-Content -LiteralPath (Join-Path $data 'worldaudit-server-tests.enabled') -Value 'disposable test world only'
$info = [Diagnostics.ProcessStartInfo]::new($serverExe)
$info.WorkingDirectory = $GameDir
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$info.RedirectStandardInput = $true
$info.RedirectStandardOutput = $true
$info.RedirectStandardError = $true
foreach ($argument in @('--dataPath', $data, '--ip', '127.0.0.1', '--port', "$Port", '--reducedthreads')) { $info.ArgumentList.Add($argument) }
$process = [Diagnostics.Process]::Start($info)
$consoleOutput = $process.StandardOutput.ReadToEndAsync()
$consoleErrors = $process.StandardError.ReadToEndAsync()
$mainLog = Join-Path $data 'Logs/server-main.log'
$reportPath = Join-Path $data 'worldaudit-server-tests.json'
$deadline = [DateTime]::UtcNow.AddMinutes(5)
try {
    do {
        if ($process.HasExited) { throw "Server exited with code $($process.ExitCode); see $mainLog" }
        if ([DateTime]::UtcNow -gt $deadline) { throw "Startup timed out; see $mainLog" }
        Start-Sleep -Milliseconds 500
    } until ((Test-Path -LiteralPath $mainLog) -and ((Get-Content -LiteralPath $mainLog -Raw) -match 'Entering runphase RunGame'))
    $process.StandardInput.WriteLine('/watest')
    do {
        if ($process.HasExited) { throw "Server exited during tests; see $mainLog" }
        if ([DateTime]::UtcNow -gt $deadline) { throw "Tests timed out; see $mainLog" }
        Start-Sleep -Milliseconds 500
    } until (Test-Path -LiteralPath $reportPath)
} finally {
    if (!$process.HasExited) {
        $process.StandardInput.WriteLine('/stop')
        if (!$process.WaitForExit(60000)) { $process.Kill(); throw "Disposable server did not stop cleanly; see $mainLog" }
    }
    [IO.File]::WriteAllText((Join-Path $run 'console.log'), $consoleOutput.GetAwaiter().GetResult())
    [IO.File]::WriteAllText((Join-Path $run 'stderr.log'), $consoleErrors.GetAwaiter().GetResult())
    $process.Dispose()
}
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
$report.results | Format-Table name, passed -AutoSize
Write-Output "Report: $reportPath"
if ($report.game -ne '1.22.7') { throw "Unexpected game version $($report.game)" }
if (@($report.results | Where-Object { !$_.passed }).Count -gt 0) { throw 'Live server tests failed.' }
if ((Get-Content -LiteralPath $mainLog -Raw) -match '\[Error\]|\[Fatal\]') { throw "Server logged errors; see $mainLog" }
