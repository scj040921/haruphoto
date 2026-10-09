param(
    [Parameter(Mandatory=$true)][int]$ProcessId,
    [Parameter(Mandatory=$true)][string]$InstallRoot,
    [Parameter(Mandatory=$true)][string]$PackagePath,
    [Parameter(Mandatory=$true)][string]$ExpectedSha256,
    [Parameter(Mandatory=$true)][string]$Version,
    [Parameter(Mandatory=$true)][string]$ReadyFile,
    [switch]$TestMode,
    [switch]$FailAfterReplace
)
$ErrorActionPreference = 'Stop'
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
$work = [IO.Path]::GetFullPath((Split-Path -Parent $PackagePath))
$prefix = Join-Path ([IO.Path]::GetTempPath()) 'haruphoto-update-'
if (-not $work.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or $work.Substring($prefix.Length) -notmatch '^[a-fA-F0-9]{32}$') { throw 'Invalid update workspace.' }
if ([IO.Path]::GetFullPath($ReadyFile) -ne (Join-Path $work 'ready.txt')) { throw 'Invalid ready signal path.' }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid version.' }
foreach ($path in @($InstallRoot, (Split-Path -Parent $InstallRoot), $work)) {
    if ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Directory links are not valid update targets.' }
}
if (-not (Test-Path -LiteralPath (Join-Path $InstallRoot 'PhotoAlbum.exe')) -or -not (Test-Path -LiteralPath (Join-Path $InstallRoot 'PhotoAlbum.dll'))) { throw 'Not a haruphoto installation.' }
if ($TestMode) {
    if (-not $InstallRoot.StartsWith((Join-Path ([IO.Path]::GetTempPath()) 'haruphoto-updater-test-'), [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid isolated update test target.' }
} else {
    $process = Get-Process -Id $ProcessId -ErrorAction Stop
    if (-not [string]::Equals($process.Path, (Join-Path $InstallRoot 'PhotoAlbum.exe'), [StringComparison]::OrdinalIgnoreCase)) { throw 'Process does not match installation.' }
}
$stage = $InstallRoot + '.stage-' + [Guid]::NewGuid().ToString('N')
$backup = $InstallRoot + '.previous-' + [Guid]::NewGuid().ToString('N')
$failed = $InstallRoot + '.failed-' + [Guid]::NewGuid().ToString('N')
$movedOld = $false
$committed = $false
$newProcess = $null
function Remove-OwnedFolder([string]$path, [string]$allowedPrefix) {
    $full = [IO.Path]::GetFullPath($path)
    if (-not $full.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase) -or $full.Substring($allowedPrefix.Length) -notmatch '^[a-fA-F0-9]{32}$') { throw 'Unsafe cleanup target.' }
    if (Test-Path -LiteralPath $full) {
        if ((Get-Item -LiteralPath $full).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing to remove a directory link.' }
        Remove-Item -LiteralPath $full -Recurse -Force
    }
}
try {
    $expected = $ExpectedSha256.Replace('sha256:', '').ToLowerInvariant()
    if ($expected -notmatch '^[a-f0-9]{64}$') { throw 'SHA256 is required.' }
    if ((Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw 'Package SHA256 mismatch.' }
    New-Item -ItemType Directory -Path $stage | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        foreach ($entry in $zip.Entries) {
            $target = [IO.Path]::GetFullPath((Join-Path $stage $entry.FullName))
            if (-not $target.StartsWith($stage + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe ZIP path.' }
            if ($entry.FullName.EndsWith('/')) { New-Item -ItemType Directory -Force -Path $target | Out-Null; continue }
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target)
        }
    } finally { $zip.Dispose() }
    $manifest = Get-Content -LiteralPath (Join-Path $stage 'package-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($manifest.version -ne $Version -or $manifest.architecture -ne 'win-x64') { throw 'Package version/architecture mismatch.' }
    foreach ($file in $manifest.files) {
        $target = [IO.Path]::GetFullPath((Join-Path $stage $file.path))
        if (-not $target.StartsWith($stage + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe manifest path.' }
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.sha256) { throw ('Corrupt package file: ' + $file.path) }
    }
    foreach ($name in @('PhotoAlbum.exe','PhotoAlbum.dll','PhotoAlbum.pri','coreclr.dll','hostfxr.dll','hostpolicy.dll','System.Private.CoreLib.dll','Microsoft.UI.Xaml.dll','vcruntime140.dll','vcruntime140_1.dll','msvcp140.dll','tools/update.ps1','tools/uninstall.ps1')) {
        if (-not (Test-Path -LiteralPath (Join-Path $stage $name))) { throw ('Missing runtime: ' + $name) }
    }
    $config = Get-Content -LiteralPath (Join-Path $stage 'PhotoAlbum.runtimeconfig.json') -Raw | ConvertFrom-Json
    if ($config.runtimeOptions.framework -or $config.runtimeOptions.frameworks) { throw 'Package is not self-contained.' }
    [IO.File]::WriteAllText($ReadyFile, 'ready')
    if (-not $TestMode) {
        Wait-Process -Id $ProcessId -Timeout 90 -ErrorAction SilentlyContinue
        if (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) { throw 'Application has not exited.' }
    }
    Move-Item -LiteralPath $InstallRoot -Destination $backup
    $movedOld = $true
    Move-Item -LiteralPath $stage -Destination $InstallRoot
    $committed = $true
    if ($FailAfterReplace) { throw 'Simulated launch failure.' }
    if (-not $TestMode) {
        $newProcess = Start-Process -FilePath (Join-Path $InstallRoot 'PhotoAlbum.exe') -WorkingDirectory $InstallRoot -PassThru
        Start-Sleep -Seconds 5
        if ($newProcess.HasExited) { throw ('Updated application exited before becoming ready: ' + $newProcess.ExitCode) }
        $registered = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\haruphoto' -ErrorAction SilentlyContinue
        if ($registered.InstallLocation -eq $InstallRoot) { Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\haruphoto' -Name DisplayVersion -Value $Version }
    }
    try { Remove-OwnedFolder $backup ($InstallRoot + '.previous-') } catch { Write-Warning 'Update succeeded; old backup retained.' }
}
catch {
    $failure = $_.Exception.Message
    if ($newProcess -and -not $newProcess.HasExited) { $newProcess.Kill(); $newProcess.WaitForExit() }
    if ($movedOld) {
        if ($committed) { Move-Item -LiteralPath $InstallRoot -Destination $failed }
        Move-Item -LiteralPath $backup -Destination $InstallRoot
        if (-not $TestMode) { Start-Process -FilePath (Join-Path $InstallRoot 'PhotoAlbum.exe') -WorkingDirectory $InstallRoot | Out-Null }
    }
    [IO.File]::WriteAllText($ReadyFile, 'Update failed: ' + $failure)
    $log = Join-Path ([IO.Path]::GetTempPath()) ('haruphoto-update-failure-' + [Guid]::NewGuid().ToString('N') + '.log')
    [IO.File]::WriteAllText($log, $failure)
    if (-not $TestMode) {
        Add-Type -AssemblyName PresentationFramework
        [System.Windows.MessageBox]::Show(('Update failed. The previous version was preserved/restored. Log: ' + $log + [Environment]::NewLine + $failure), 'haruphoto update') | Out-Null
    }
    throw
}
finally {
    Remove-OwnedFolder $stage ($InstallRoot + '.stage-')
    # Failed payloads remain available for diagnosis; user photos/settings are never touched.
    Remove-OwnedFolder $work $prefix
}
