param(
    [string]$Package = "",
    [string]$InstallRoot = "$env:LOCALAPPDATA\Programs\haruphoto",
    [switch]$NoLaunch
)

$ErrorActionPreference = "Stop"
$AppName = "haruphoto"
$ExeName = "PhotoAlbum.exe"
$ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$TempRoot = Join-Path $env:TEMP "haruphoto-install-$([Guid]::NewGuid().ToString('N'))"
$StageRoot = Join-Path $TempRoot "stage"
$BackupRoot = "$InstallRoot.previous"

function Get-PackagePath {
    if ($Package) { return (Resolve-Path $Package).Path }
    $zip = Get-ChildItem $ScriptRoot -Filter "haruphoto-*-payload.zip" -File | Select-Object -First 1
    if (-not $zip) { throw "未找到安装 ZIP。请使用 -Package 指定 haruphoto ZIP 文件。" }
    return $zip.FullName
}

function New-Shortcut([string]$Path, [string]$Target) {
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($Path)
    $shortcut.TargetPath = $Target
    $shortcut.WorkingDirectory = Split-Path $Target
    $shortcut.IconLocation = "$Target,0"
    $shortcut.Save()
}

try {
    $packagePath = Get-PackagePath
    New-Item -ItemType Directory -Force -Path $StageRoot | Out-Null
    Expand-Archive -LiteralPath $packagePath -DestinationPath $StageRoot -Force

    $exe = Get-ChildItem $StageRoot -Filter $ExeName -Recurse -File | Select-Object -First 1
    if (-not $exe) { throw "安装包缺少 $ExeName。" }
    $payload = $exe.Directory.FullName
    $installedVersion = $exe.VersionInfo.ProductVersion
    if ([string]::IsNullOrWhiteSpace($installedVersion)) { $installedVersion = "1.2.0" }

    Get-Process PhotoAlbum -ErrorAction SilentlyContinue | Stop-Process -Force
    if (Test-Path $BackupRoot) { Remove-Item $BackupRoot -Recurse -Force }
    if (Test-Path $InstallRoot) { Move-Item $InstallRoot $BackupRoot }
    New-Item -ItemType Directory -Force -Path $InstallRoot | Out-Null
    Copy-Item (Join-Path $payload '*') $InstallRoot -Recurse -Force

    $toolsTarget = Join-Path $InstallRoot "tools"
    New-Item -ItemType Directory -Force -Path $toolsTarget | Out-Null
    $uninstallScript = Join-Path $ScriptRoot "uninstall.ps1"
    $updateScript = Join-Path $ScriptRoot "update.ps1"
    if (-not (Test-Path $uninstallScript)) { throw "安装包缺少 uninstall.ps1。" }
    if (-not (Test-Path $updateScript)) { throw "安装包缺少 update.ps1。" }
    Copy-Item $uninstallScript (Join-Path $toolsTarget "uninstall.ps1") -Force
    Copy-Item $updateScript (Join-Path $toolsTarget "update.ps1") -Force

    $startMenu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
    $desktop = [Environment]::GetFolderPath('Desktop')
    New-Item -ItemType Directory -Force -Path $startMenu | Out-Null
    New-Shortcut (Join-Path $startMenu "$AppName.lnk") (Join-Path $InstallRoot $ExeName)
    New-Shortcut (Join-Path $desktop "$AppName.lnk") (Join-Path $InstallRoot $ExeName)

    $uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\haruphoto"
    New-Item -Path $uninstallKey -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name DisplayName -Value $AppName -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name DisplayVersion -Value $installedVersion -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name InstallLocation -Value $InstallRoot -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name Publisher -Value "scj040921" -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name UninstallString -Value "powershell.exe -ExecutionPolicy Bypass -File `"$InstallRoot\tools\uninstall.ps1`"" -Force | Out-Null

    if (Test-Path $BackupRoot) { Remove-Item $BackupRoot -Recurse -Force }
    if (-not $NoLaunch) { Start-Process (Join-Path $InstallRoot $ExeName) }
    Write-Output "安装完成：$InstallRoot"
}
catch {
    if (Test-Path $BackupRoot) {
        if (Test-Path $InstallRoot) { Remove-Item $InstallRoot -Recurse -Force -ErrorAction SilentlyContinue }
        Move-Item $BackupRoot $InstallRoot -Force
    }
    throw
}
finally {
    if (Test-Path $TempRoot) { Remove-Item $TempRoot -Recurse -Force -ErrorAction SilentlyContinue }
}
