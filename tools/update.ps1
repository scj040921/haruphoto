param(
    [Parameter(Mandatory = $true)][int]$ProcessId,
    [Parameter(Mandatory = $true)][string]$InstallRoot,
    [Parameter(Mandatory = $true)][string]$PackageUrl,
    [Parameter(Mandatory = $true)][string]$ExpectedSha256,
    [Parameter(Mandatory = $true)][string]$Version
)

$ErrorActionPreference = "Stop"
$TempRoot = Join-Path $env:TEMP "haruphoto-update-$([Guid]::NewGuid().ToString('N'))"
$ZipPath = Join-Path $TempRoot "haruphoto-$Version.zip"
$StageRoot = Join-Path $TempRoot "stage"
$BackupRoot = "$InstallRoot.backup-$Version"
$ExeName = "PhotoAlbum.exe"

function Restore-Backup {
    if (Test-Path $BackupRoot) {
        if (Test-Path $InstallRoot) { Remove-Item $InstallRoot -Recurse -Force -ErrorAction SilentlyContinue }
        Move-Item $BackupRoot $InstallRoot -Force
    }
}

try {
    New-Item -ItemType Directory -Force -Path $TempRoot, $StageRoot | Out-Null
    Invoke-WebRequest -Uri $PackageUrl -OutFile $ZipPath -UseBasicParsing

    $actual = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $expected = $ExpectedSha256.ToLowerInvariant().Replace('sha256:', '')
    if ($actual -ne $expected) { throw "更新包校验失败：SHA256 不匹配。" }

    Expand-Archive -LiteralPath $ZipPath -DestinationPath $StageRoot -Force
    $exe = Get-ChildItem $StageRoot -Filter $ExeName -Recurse -File | Select-Object -First 1
    if (-not $exe) { throw "更新包缺少 $ExeName。" }
    $payload = $exe.Directory.FullName

    $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if ($process) { Wait-Process -Id $ProcessId -Timeout 45 -ErrorAction SilentlyContinue }
    if (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) { throw "等待 haruphoto 退出超时。" }

    if (Test-Path $BackupRoot) { Remove-Item $BackupRoot -Recurse -Force }
    Copy-Item $InstallRoot $BackupRoot -Recurse -Force
    Copy-Item (Join-Path $payload '*') $InstallRoot -Recurse -Force

    $newExe = Join-Path $InstallRoot $ExeName
    if (-not (Test-Path $newExe)) { throw "更新后找不到主程序。" }
    Start-Process $newExe
    Remove-Item $BackupRoot -Recurse -Force -ErrorAction SilentlyContinue
}
catch {
    Restore-Backup
    [System.Windows.Forms.MessageBox]::Show("haruphoto 更新失败，已尝试恢复旧版本。`n$($_.Exception.Message)", "haruphoto 更新", 'OK', 'Error') | Out-Null
    throw
}
finally {
    if (Test-Path $TempRoot) { Remove-Item $TempRoot -Recurse -Force -ErrorAction SilentlyContinue }
}
