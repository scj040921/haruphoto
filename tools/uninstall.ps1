param([switch]$KeepProgramFiles)

$ErrorActionPreference = "Stop"
$AppName = "haruphoto"
$InstallRoot = "$env:LOCALAPPDATA\Programs\haruphoto"
$ExeName = "PhotoAlbum.exe"

$InstallRoot = [IO.Path]::GetFullPath($InstallRoot)
if ($InstallRoot -ne [IO.Path]::GetFullPath("$env:LOCALAPPDATA\Programs\haruphoto")) { throw 'Invalid uninstall directory.' }
if ((Test-Path -LiteralPath $InstallRoot) -and ((Get-Item -LiteralPath $InstallRoot).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Refusing to uninstall a directory link.' }
Get-Process PhotoAlbum -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Path -eq (Join-Path $InstallRoot $ExeName) } catch { $false }
} | Stop-Process -Force

$startMenu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\$AppName.lnk"
$desktop = Join-Path ([Environment]::GetFolderPath('Desktop')) "$AppName.lnk"
Remove-Item $startMenu -Force -ErrorAction SilentlyContinue
Remove-Item $desktop -Force -ErrorAction SilentlyContinue
Remove-Item "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\haruphoto" -Recurse -Force -ErrorAction SilentlyContinue

if (-not $KeepProgramFiles -and (Test-Path $InstallRoot)) {
    Remove-Item -LiteralPath $InstallRoot -Recurse -Force
}

Write-Output "已卸载 haruphoto。照片库设置和本地索引保留在 %LOCALAPPDATA%\haruphoto，未被删除。"
