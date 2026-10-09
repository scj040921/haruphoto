param([string]$Package = "$PSScriptRoot/../release/haruphoto-v1.3.2-portable.zip", [string]$Version = '1.3.2')
$ErrorActionPreference = 'Stop'
$Package = [IO.Path]::GetFullPath($Package)
$script = [IO.Path]::GetFullPath("$PSScriptRoot/../tools/update.ps1")
$hash = (Get-FileHash -LiteralPath $Package -Algorithm SHA256).Hash
foreach ($mode in @('success','bad-hash','rollback','traversal')) {
    $target = Join-Path ([IO.Path]::GetTempPath()) ('haruphoto-updater-test-' + [Guid]::NewGuid().ToString('N'))
    $work = Join-Path ([IO.Path]::GetTempPath()) ('haruphoto-update-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $target,$work | Out-Null
    try {
        [IO.File]::WriteAllText((Join-Path $target 'PhotoAlbum.exe'),'old-exe')
        [IO.File]::WriteAllText((Join-Path $target 'PhotoAlbum.dll'),'old-dll')
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
        if ($mode -eq 'rollback') { $arguments += '-FailAfterReplace' }
        & "$env:SystemRoot/System32/WindowsPowerShell/v1.0/powershell.exe" @arguments 2>&1 | Out-Host
        $code = $LASTEXITCODE
        if ($mode -eq 'success') {
            if ($code -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $target 'package-manifest.json'))) { throw 'Fresh update failed.' }
        } else {
            if ($code -eq 0 -or [IO.File]::ReadAllText((Join-Path $target 'PhotoAlbum.exe')) -ne 'old-exe') { throw "Old version was not preserved: $mode" }
        }
        if (Test-Path -LiteralPath $work) { throw 'Update workspace leaked.' }
        if (Get-ChildItem -LiteralPath ([IO.Path]::GetTempPath()) -Directory | Where-Object Name -like ((Split-Path $target -Leaf) + '.stage-*')) { throw 'Stage leaked.' }
        Write-Output "PASS updater $mode (Windows PowerShell 5.1)"
    } finally {
        $targets = @($target,$work) + @(Get-ChildItem -LiteralPath ([IO.Path]::GetTempPath()) -Directory | Where-Object Name -like ((Split-Path $target -Leaf) + '.failed-*') | Select-Object -ExpandProperty FullName)
        foreach ($owned in $targets) {
            $full = [IO.Path]::GetFullPath($owned)
            if (-not $full.StartsWith($target,[StringComparison]::OrdinalIgnoreCase) -and $full -ne $work) { throw 'Unsafe test cleanup.' }
            if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
        }
    }
}
exit 0
