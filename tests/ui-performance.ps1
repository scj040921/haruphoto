param([Parameter(Mandatory=$true)][string]$AppExe)
$ErrorActionPreference='Stop'
$lines=& "$PSScriptRoot/../tools/create-ui-fixture.ps1" -AppExe $AppExe -PhotoCount 420 -PerformanceSmoke
$profile=($lines | Where-Object {$_ -like 'QA profile: *'}) -replace '^QA profile: ',''
$idValue=($lines | Where-Object {$_ -like 'QA process: *'}) -replace '^QA process: ',''
if(-not $profile -or -not $idValue){throw 'QA fixture did not start.'}
$report=Join-Path $profile 'performance-smoke.json'
$deadline=(Get-Date).AddMinutes(3)
while(-not (Test-Path -LiteralPath $report)){
    if(-not (Get-Process -Id ([int]$idValue) -ErrorAction SilentlyContinue)){throw "QA app exited without report: $profile"}
    if((Get-Date) -gt $deadline){throw "QA timeout: $profile"}
    Start-Sleep -Milliseconds 500
}
$result=Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
$result | ConvertTo-Json -Depth 5
if(-not $result.success){throw "UI regression failed; report at $report"}
Write-Output "PASS WinUI performance smoke: $($result.checks.Count) checks; report $report"
