param([string]$Package = '', [string]$Version = '', [string[]]$Modes = @('success','bad-hash','rollback','traversal','wrong-channel','wrong-tag','directory-lock','directory-lock-rollback','directory-and-file-lock'))
$ErrorActionPreference = 'Stop'
if (-not $Version) { [xml]$appProject = Get-Content -LiteralPath "$PSScriptRoot/../PhotoAlbum.csproj"; $Version = [string]($appProject.Project.PropertyGroup | Select-Object -First 1).Version }
if (-not $Package) { $Package = "$PSScriptRoot/../release/haruphoto-v$Version-portable.zip" }
$Package = [IO.Path]::GetFullPath($Package)
$script = [IO.Path]::GetFullPath("$PSScriptRoot/../tools/update.ps1")
$hash = (Get-FileHash -LiteralPath $Package -Algorithm SHA256).Hash
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class UpdateDirectoryLock {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    public static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    public static SafeFileHandle Open(string path) {
        var handle = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return handle;
    }
}
'@
$packageZip = [IO.Compression.ZipFile]::OpenRead($Package)
try { $reader = [IO.StreamReader]::new($packageZip.GetEntry('package-manifest.json').Open()); try { $identity = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() } } finally { $packageZip.Dispose() }
foreach ($mode in $Modes) {
    $target = Join-Path ([IO.Path]::GetTempPath()) ('haruphoto-updater-test-' + [Guid]::NewGuid().ToString('N'))
    $work = Join-Path ([IO.Path]::GetTempPath()) ('haruphoto-update-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $target,$work | Out-Null
    $directoryLock = $null
    $fileLock = $null
    try {
        [IO.File]::WriteAllText((Join-Path $target 'PhotoAlbum.exe'),'old-exe')
        [IO.File]::WriteAllText((Join-Path $target 'PhotoAlbum.dll'),'old-dll')
        if ($mode -like 'directory-*') {
            [IO.File]::WriteAllText((Join-Path $target '00-rollback-marker.txt'),'old-marker')
            $directoryLock = [UpdateDirectoryLock]::Open($target)
            if ($mode -eq 'directory-and-file-lock') {
                $fileLock = [IO.File]::Open((Join-Path $target 'PhotoAlbum.dll'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
            }
        }
        $payload = Join-Path $work 'package.zip'
        $expected = $hash
        if ($mode -eq 'traversal') {
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            $zip = [IO.Compression.ZipFile]::Open($payload,[IO.Compression.ZipArchiveMode]::Create)
            try { $entry = $zip.CreateEntry('../escape.txt'); $writer = [IO.StreamWriter]::new($entry.Open()); $writer.Write('unsafe'); $writer.Dispose() } finally { $zip.Dispose() }
            $expected = (Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash
        } else { Copy-Item -LiteralPath $Package -Destination $payload }
        if ($mode -eq 'bad-hash') { $expected = ('0' * 64) }
        $arguments = @('-NoProfile','-ExecutionPolicy','Bypass','-File',$script,'-ProcessId','0','-InstallRoot',$target,'-PackagePath',$payload,'-ExpectedSha256',$expected,'-Version',$Version,'-ReadyFile',(Join-Path $work 'ready.txt'),'-TestMode')
        if ($identity.channel) {
            $channel = if ($mode -eq 'wrong-channel') { if ($identity.channel -eq 'Stable') { 'Nightly' } else { 'Stable' } } else { $identity.channel }
            $arguments += @('-ExpectedChannel',$channel)
        }
        if ($identity.tag) { $arguments += @('-ExpectedTag',$(if ($mode -eq 'wrong-tag') { 'v0.0.0' } else { $identity.tag })) }
        if ($mode -in @('rollback','directory-lock-rollback')) { $arguments += '-FailAfterReplace' }
        $updaterOutput = & "$env:SystemRoot/System32/WindowsPowerShell/v1.0/powershell.exe" @arguments 2>&1
        $code = $LASTEXITCODE
        if ($mode -in @('success','directory-lock')) {
            if ($code -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $target 'package-manifest.json'))) { throw 'Fresh update failed.' }
        } else {
            if ($code -eq 0 -or [IO.File]::ReadAllText((Join-Path $target 'PhotoAlbum.exe')) -ne 'old-exe') { throw "Old version was not preserved: $mode" }
            if ($mode -like 'directory-*' -and (-not (Test-Path -LiteralPath (Join-Path $target '00-rollback-marker.txt')) -or [IO.File]::ReadAllText((Join-Path $target 'PhotoAlbum.dll')) -ne 'old-dll' -or [IO.File]::ReadAllText((Join-Path $target '00-rollback-marker.txt')) -ne 'old-marker')) {
                $updaterOutput | Write-Output
                throw "Partial file replacement was not rolled back: $mode"
            }
        }
        if (Test-Path -LiteralPath $work) { throw 'Update workspace leaked.' }
        if (Get-ChildItem -LiteralPath ([IO.Path]::GetTempPath()) -Directory | Where-Object Name -like ((Split-Path $target -Leaf) + '.stage-*')) { throw 'Stage leaked.' }
        Write-Output "PASS updater $mode (Windows PowerShell 5.1)"
    } finally {
        if ($fileLock) { $fileLock.Dispose() }
        if ($directoryLock) { $directoryLock.Dispose() }
        $targets = @($target,$work) + @(Get-ChildItem -LiteralPath ([IO.Path]::GetTempPath()) -Directory | Where-Object Name -like ((Split-Path $target -Leaf) + '.failed-*') | Select-Object -ExpandProperty FullName)
        foreach ($owned in $targets) {
            $full = [IO.Path]::GetFullPath($owned)
            if (-not $full.StartsWith($target,[StringComparison]::OrdinalIgnoreCase) -and $full -ne $work) { throw 'Unsafe test cleanup.' }
            if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
        }
    }
}
exit 0
