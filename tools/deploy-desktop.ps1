param(
    [Parameter(Mandatory=$true)][string]$AppOutput,
    [string]$RetainedArtifactSource='',
    [string]$ReportPath="$PSScriptRoot/../artifacts/deployment.json"
)
$ErrorActionPreference='Stop'
$target=Join-Path ([Environment]::GetFolderPath('Desktop')) 'haruphoto'
$target=[IO.Path]::GetFullPath($target)
$output=[IO.Path]::GetFullPath($AppOutput)
$exe=Join-Path $target 'PhotoAlbum.exe'
if(-not (Test-Path -LiteralPath $exe)){throw 'Confirmed desktop application not found.'}
if(Test-Path -LiteralPath (Join-Path $target '.git')){throw 'Refusing to deploy into a source checkout.'}
foreach($name in @('PhotoAlbum.exe','PhotoAlbum.dll','PhotoAlbum.pri','coreclr.dll','hostfxr.dll','vcruntime140.dll')){
    if(-not (Test-Path -LiteralPath (Join-Path $output $name))){throw "Incomplete application: $name"}
}
if(Test-Path -LiteralPath (Join-Path $output 'release')){throw 'Recursive development output is still present.'}
if([Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes((Join-Path $output 'PhotoAlbum.dll'))).Contains('RunPerformanceSmokeAsync')){throw 'Cannot deploy a QA-only build.'}
$files=@(Get-ChildItem -LiteralPath $output -Recurse -File -Force)
if(Get-ChildItem -LiteralPath $output -Recurse -Force | Where-Object {($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0}){throw 'Build contains a reparse point.'}
$beforeBytes=(Get-ChildItem -LiteralPath $target -Recurse -File -Force | Measure-Object Length -Sum).Sum
$backup=Join-Path ([IO.Path]::GetTempPath()) ('haruphoto-desktop-rollback-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $backup | Out-Null
$changed=@(); $introduced=@()
foreach($file in $files){
    $relative=$file.FullName.Substring($output.Length+1)
    $existing=Join-Path $target $relative
    if(Test-Path -LiteralPath $existing -PathType Leaf){
        if((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $existing -Algorithm SHA256).Hash){
            $saved=Join-Path $backup $relative
            New-Item -ItemType Directory -Path (Split-Path $saved -Parent) -Force | Out-Null
            Copy-Item -LiteralPath $existing -Destination $saved
            $changed+=$relative
        }
    } else {$introduced+=$relative}
}
[IO.File]::WriteAllText((Join-Path $backup 'rollback.json'),(@{target=$target;changed=$changed;introduced=$introduced}|ConvertTo-Json -Depth 4))
$running=@(Get-Process PhotoAlbum -ErrorAction SilentlyContinue | Where-Object Path -eq $exe)
foreach($process in $running){if(-not $process.CloseMainWindow() -or -not $process.WaitForExit(15000)){throw 'Desktop application did not finish a graceful close.'}}
try{
    Get-ChildItem -LiteralPath $output -Force | ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination $target -Recurse -Force}
    foreach($name in @('PhotoAlbum.dll','PhotoAlbum.pri','MainWindow.xbf','App.xbf','tools/update.ps1')){
        if((Get-FileHash -LiteralPath (Join-Path $output $name) -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath (Join-Path $target $name) -Algorithm SHA256).Hash){throw "Copy mismatch: $name"}
    }
    $process=Start-Process -FilePath $exe -WorkingDirectory $target -PassThru
    $deadline=(Get-Date).AddSeconds(25)
    do{Start-Sleep -Milliseconds 500;$process.Refresh()}until($process.HasExited -or $process.MainWindowHandle -ne 0 -or (Get-Date) -gt $deadline)
    if($process.HasExited -or $process.MainWindowHandle -eq 0){throw 'New application did not create a window.'}
    if((Get-Process -Id $process.Id).Path -ne $exe){throw 'New process is not the desktop application.'}
}catch{
    if($process -and -not $process.HasExited -and $process.Path -eq $exe){Stop-Process -Id $process.Id}
    foreach($relative in $changed){Copy-Item -LiteralPath (Join-Path $backup $relative) -Destination (Join-Path $target $relative) -Force}
    throw
}

# 仅移除哈希确认相同且保留原件的、曾被误打包的 release 目录。
$removedBytes=0; $removedFiles=0
if($RetainedArtifactSource){
    $redundant=Join-Path $target 'release'
    $retained=[IO.Path]::GetFullPath($RetainedArtifactSource)
    if(Test-Path -LiteralPath $redundant){
        if($retained.StartsWith($redundant,[StringComparison]::OrdinalIgnoreCase) -or ((Get-Item -LiteralPath $redundant).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Artifact cleanup does not have an independent retained directory.'}
        $copied=@(Get-ChildItem -LiteralPath $redundant -Recurse -File -Force)
        if(Get-ChildItem -LiteralPath $redundant -Recurse -Force | Where-Object {($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0}){throw 'Copied artifact has a reparse point.'}
        foreach($file in $copied){
            $other=Join-Path $retained $file.FullName.Substring($redundant.Length+1)
            if(-not (Test-Path -LiteralPath $other -PathType Leaf) -or (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $other -Algorithm SHA256).Hash){throw 'Copied artifact lacks an identical retained counterpart.'}
        }
        $data=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'haruphoto'
        $library=Get-Content -LiteralPath (Join-Path $data 'library.json') -Raw | ConvertFrom-Json
        $settings=Get-Content -LiteralPath (Join-Path $data 'settings.json') -Raw | ConvertFrom-Json
        if($library.photos | Where-Object {$_.path.StartsWith($redundant,[StringComparison]::OrdinalIgnoreCase)}){throw 'Library references copied artifacts.'}
        if(($settings.BackgroundImagePath -and $settings.BackgroundImagePath.StartsWith($redundant,[StringComparison]::OrdinalIgnoreCase)) -or ($settings.WatchedFolders | Where-Object {$_.StartsWith($redundant,[StringComparison]::OrdinalIgnoreCase)})){throw 'Settings reference copied artifacts.'}
        if((Get-Item -LiteralPath $redundant).FullName -ne (Join-Path $target 'release')){throw 'Unexpected cleanup target.'}
        $removedBytes=($copied|Measure-Object Length -Sum).Sum; $removedFiles=$copied.Count
        Remove-Item -LiteralPath $redundant -Recurse -Force
    }
}
$report=[ordered]@{target=$target;version=(Get-Item -LiteralPath $exe).VersionInfo.FileVersion;processId=$process.Id;
    beforeBytes=$beforeBytes;afterBytes=(Get-ChildItem -LiteralPath $target -Recurse -File -Force|Measure-Object Length -Sum).Sum;
    removedGeneratedFiles=$removedFiles;removedGeneratedBytes=$removedBytes;rollback=$backup;
    backupBytes=(Get-ChildItem -LiteralPath $backup -Recurse -File|Measure-Object Length -Sum).Sum;backupChangedFiles=$changed.Count}
New-Item -ItemType Directory -Path (Split-Path ([IO.Path]::GetFullPath($ReportPath)) -Parent) -Force | Out-Null
[IO.File]::WriteAllText([IO.Path]::GetFullPath($ReportPath),($report|ConvertTo-Json))
$report|ConvertTo-Json
