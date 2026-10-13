param([Parameter(Mandatory=$true)][string]$Profile,[Parameter(Mandatory=$true)][int]$ParentId,[string]$Package='',[switch]$SimulateFailure)
$ErrorActionPreference='Stop'
if (-not $Package) { [xml]$appProject = Get-Content -LiteralPath "$PSScriptRoot/../PhotoAlbum.csproj"; $Package = "$PSScriptRoot/../release/haruphoto-v$([string]($appProject.Project.PropertyGroup | Select-Object -First 1).Version)-portable.zip" }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive=[IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($Package))
try { $reader=[IO.StreamReader]::new($archive.GetEntry('package-manifest.json').Open()); try { $identity=$reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() } } finally { $archive.Dispose() }
$Profile=[IO.Path]::GetFullPath($Profile)
$prefix=Join-Path ([IO.Path]::GetTempPath()) 'haruphoto-ui-test-'
if (-not $Profile.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or $Profile.Substring($prefix.Length) -notmatch '^[a-f0-9]{32}$') { throw 'Not an owned QA profile.' }
$install=Join-Path $Profile 'app'
$parent=Get-Process -Id $ParentId
if ($parent.Path -ne (Join-Path $install 'PhotoAlbum.exe')) { throw 'Parent is not the QA app.' }
$work=Join-Path ([IO.Path]::GetTempPath()) ('haruphoto-update-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$payload=Join-Path $work 'package.zip'
Copy-Item -LiteralPath ([IO.Path]::GetFullPath($Package)) -Destination $payload
$hash=(Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash
$marker=Join-Path $install 'old-install-marker.txt'
[IO.File]::WriteAllText($marker,'old installation')
$start=[Diagnostics.ProcessStartInfo]::new()
$start.FileName=Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WorkingDirectory=$work
$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
$start.Environment['HARUPHOTO_TEST_DATA']=$Profile
foreach ($arg in @('-NoProfile','-ExecutionPolicy','Bypass','-File',[IO.Path]::GetFullPath("$PSScriptRoot/../tools/update.ps1"),'-ProcessId',"$ParentId",'-InstallRoot',$install,'-PackagePath',$payload,'-ExpectedSha256',$hash,'-Version',$identity.version,'-ExpectedChannel',$identity.channel,'-ExpectedTag',$identity.tag,'-ReadyFile',(Join-Path $work 'ready.txt'))) { $start.ArgumentList.Add([string]$arg) }
if ($SimulateFailure) { $start.ArgumentList.Add('-FailAfterReplace') }
$helper=[Diagnostics.Process]::Start($start)
$standardOutput=$helper.StandardOutput.ReadToEndAsync();$standardError=$helper.StandardError.ReadToEndAsync()
$watch=[Diagnostics.Stopwatch]::StartNew()
while (-not (Test-Path -LiteralPath (Join-Path $work 'ready.txt'))) {
    if ($helper.HasExited -or $watch.Elapsed.TotalSeconds -gt 60) { throw 'Updater preflight failed.' }
    Start-Sleep -Milliseconds 200
}
if ([IO.File]::ReadAllText((Join-Path $work 'ready.txt')) -ne 'ready') { throw 'Updater refused package.' }
# Only the verified, generated QA application is closed; real library processes are untouched.
Stop-Process -Id $ParentId
if (-not $helper.WaitForExit(90000)) { throw 'Updater did not finish.' }
Write-Output $standardOutput.GetAwaiter().GetResult()
Write-Output $standardError.GetAwaiter().GetResult()
$running=Get-Process PhotoAlbum -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -eq (Join-Path $install 'PhotoAlbum.exe') } catch { $false } }
if (-not $running) { throw 'No restarted QA process.' }
if (@($running).Count -ne 1) { throw 'Expected one restarted QA process.' }
$windowDeadline=(Get-Date).AddSeconds(20)
do { $running.Refresh(); if ($running.HasExited) { throw 'Restarted QA app exited before creating a window.' }; if ($running.MainWindowHandle -ne 0) { break }; Start-Sleep -Milliseconds 200 } while ((Get-Date) -lt $windowDeadline)
if ($running.MainWindowHandle -eq 0) { throw 'Restarted QA app did not create a window.' }
if (-not $SimulateFailure -and $helper.ExitCode -ne 0) { throw 'Update failed.' }
if ($SimulateFailure -and -not (Test-Path -LiteralPath $marker)) { throw 'Rollback lost original installation.' }
if (-not $SimulateFailure -and (Test-Path -LiteralPath $marker)) { throw 'Old files survived replacement.' }
if (-not (Test-Path -LiteralPath (Join-Path $Profile 'library.json'))) { throw 'QA library was removed.' }
Write-Output ("PASS real restart / rollback: failure="+$SimulateFailure+"; new QA pid="+$running.Id+"; profile="+$Profile)
