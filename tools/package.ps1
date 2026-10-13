param(
    [Parameter(Mandatory = $true)][string]$AppOutput,
    [string]$Version = '1.3.4',
    [string]$Tag = '',
    [ValidateSet('Stable','Nightly')][string]$Channel = 'Stable',
    [string]$Commit = '',
    [string]$VcRuntimeDir = '',
    [string]$Dotnet = 'dotnet',
    [switch]$Offline
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $Tag) { $Tag = "v$Version" }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid package version.' }
if ($Channel -eq 'Stable' -and ($Tag -notmatch '^[vV]\d+\.\d+\.\d+$' -or $Tag.Substring(1) -ne $Version)) { throw 'Release tag does not match package version.' }
if ($Channel -eq 'Nightly' -and $Tag -notmatch '^nightly-\d{8}-\d+$') { throw 'Invalid Nightly tag.' }
if (-not $Commit) { $Commit = (git -C $root rev-parse HEAD 2>$null) }
if ($Commit -notmatch '^[0-9a-f]{40}$') { throw 'A full source commit is required.' }
$release = Join-Path $root 'release'
$payload = Join-Path $release ('payload-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $release | Out-Null
if (Test-Path -LiteralPath $payload) { throw 'Use a clean release/payload directory to avoid packaging stale files.' }
New-Item -ItemType Directory -Path $payload | Out-Null
Copy-Item -Path (Join-Path $AppOutput '*') -Destination $payload -Recurse
$actualVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $payload 'PhotoAlbum.dll')).FileVersion
if ($actualVersion -ne ($Version + '.0')) { throw "Compiled app version $actualVersion does not match package $Version." }

if (-not $VcRuntimeDir) {
    $cache = Join-Path $release 'vclibs'
    $appx = Join-Path $release 'Microsoft.VCLibs.x64.14.00.Desktop.appx'
    if (-not (Test-Path -LiteralPath $appx)) {
        if ($Offline) { throw 'Offline build requires -VcRuntimeDir or a cached official VCLibs package.' }
        Invoke-WebRequest 'https://aka.ms/Microsoft.VCLibs.x64.14.00.Desktop.appx' -OutFile $appx -UseBasicParsing
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (-not (Test-Path -LiteralPath $cache)) { [IO.Compression.ZipFile]::ExtractToDirectory($appx, $cache) }
    $VcRuntimeDir = $cache
}
foreach ($name in @('msvcp140.dll', 'msvcp140_1.dll', 'msvcp140_2.dll', 'msvcp140_atomic_wait.dll', 'msvcp140_codecvt_ids.dll', 'vcruntime140.dll', 'vcruntime140_1.dll', 'concrt140.dll')) {
    $file = Get-ChildItem -LiteralPath $VcRuntimeDir -Recurse -Filter $name -File | Select-Object -First 1
    if ($file) { Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $payload $name) }
    elseif ($name -in @('msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll')) { throw "Missing app-local VC++ runtime: $name" }
}
foreach ($name in @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll', 'Microsoft.UI.Xaml.dll', 'PhotoAlbum.pri', 'PhotoAlbum.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $payload $name))) { throw "Missing runtime/application file: $name" }
}
$config = Get-Content -LiteralPath (Join-Path $payload 'PhotoAlbum.runtimeconfig.json') -Raw | ConvertFrom-Json
if ($config.runtimeOptions.framework -or $config.runtimeOptions.frameworks) { throw '.NET runtime was not bundled. Build with --self-contained true.' }
# PowerShell 5.1 reads BOM-less scripts as the system ANSI code page.
$tools = Join-Path $payload 'tools'
New-Item -ItemType Directory -Force -Path $tools | Out-Null
foreach ($name in @('update.ps1', 'uninstall.ps1')) {
    $text = [IO.File]::ReadAllText((Join-Path $PSScriptRoot $name), [Text.Encoding]::UTF8)
    [IO.File]::WriteAllText((Join-Path $tools $name), $text, [Text.UTF8Encoding]::new($true))
}
$build = [ordered]@{ version = $Version; channel = $Channel; tag = $Tag; commit = $Commit; builtAtUtc = [DateTime]::UtcNow.ToString('o') } | ConvertTo-Json
[IO.File]::WriteAllText((Join-Path $payload 'build-info.json'), $build, [Text.UTF8Encoding]::new($false))
$files = @(Get-ChildItem -LiteralPath $payload -Recurse -File | ForEach-Object {
    [ordered]@{ path = $_.FullName.Substring($payload.Length + 1).Replace('\','/'); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$manifest = [ordered]@{ version = $Version; channel = $Channel; tag = $Tag; commit = $Commit; architecture = 'win-x64'; files = $files } | ConvertTo-Json -Depth 5
[IO.File]::WriteAllText((Join-Path $payload 'package-manifest.json'), $manifest, [Text.UTF8Encoding]::new($false))
$zip = Join-Path $release 'payload.zip'
Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $zip -CompressionLevel Optimal -Force
$project = Join-Path $root 'installer/Setup/Setup.csproj'
$setupOut = Join-Path $release 'setup'
if ($Offline) {
    $localFeed = Join-Path $release 'empty-feed'
    New-Item -ItemType Directory -Force -Path $localFeed | Out-Null
    & $Dotnet restore $project --source $localFeed
} else { & $Dotnet restore $project }
if ($LASTEXITCODE) { throw 'Setup restore failed.' }
# Only this small installer uses publish; the WinUI app keeps its build-output workflow.
& $Dotnet publish $project -c Release --no-restore -o $setupOut "-p:Version=$Version"
if ($LASTEXITCODE) { throw 'Setup build failed.' }
$stem = if ($Channel -eq 'Stable') { "haruphoto-v$Version" } else { "haruphoto-$Tag" }
$setup = Join-Path $release "$stem-Setup.exe"
Copy-Item -LiteralPath (Join-Path $setupOut 'HaruphotoSetup.exe') -Destination $setup
$portable = Join-Path $release "$stem-portable.zip"
Copy-Item -LiteralPath $zip -Destination $portable
$update = [ordered]@{ version = $Version; tag = $Tag; channel = $Channel; commit = $Commit; architecture = 'win-x64'; url = "https://github.com/scj040921/haruphoto/releases/download/$Tag/$stem-portable.zip"; sha256 = (Get-FileHash -LiteralPath $portable -Algorithm SHA256).Hash.ToLowerInvariant(); size = (Get-Item -LiteralPath $portable).Length; notes = '完整更新内容见 GitHub Release 页面。' } | ConvertTo-Json
[IO.File]::WriteAllText((Join-Path $release 'update.json'), $update, [Text.UTF8Encoding]::new($false))
$sums = @($setup, $portable, (Join-Path $release 'update.json')) | ForEach-Object { "{0}  {1}" -f (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant(), [IO.Path]::GetFileName($_) }
[IO.File]::WriteAllLines((Join-Path $release 'SHA256SUMS.txt'), $sums, [Text.UTF8Encoding]::new($false))
Write-Output "Packages ready: $setup; $portable"
