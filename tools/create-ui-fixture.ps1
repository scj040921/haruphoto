param([Parameter(Mandatory=$true)][string]$AppExe,[ValidateRange(3,1000)][int]$PhotoCount=130,[switch]$PerformanceSmoke)
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('haruphoto-ui-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$images = Join-Path $root 'images'
New-Item -ItemType Directory -Path $images | Out-Null
Add-Type -AssemblyName System.Drawing
$photos = @()
for ($i = 0; $i -lt $PhotoCount; $i++) {
    $size = @(@(720,480),@(320,640),@(480,480))[$i % 3]
    if($PerformanceSmoke -and $i -eq 3){$size=@(64,4096)}
    if($PerformanceSmoke -and $i -eq 4){$size=@(4096,64)}
    $bitmap = [Drawing.Bitmap]::new($size[0],$size[1])
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.Clear([Drawing.Color]::FromArgb(255,50 + $i % 150,90 + $i % 110,160))
    $font = [Drawing.Font]::new('Arial',34)
    $graphics.DrawString(('TEST ' + $i),$font,[Drawing.Brushes]::White,30,30)
    $graphics.DrawRectangle([Drawing.Pens]::White,15,15,$size[0]-30,$size[1]-30)
    $path = Join-Path $images ('test-{0:D3}.png' -f $i)
    $bitmap.Save($path,[Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose(); $bitmap.Dispose(); $font.Dispose()
    $photos += @{path=$path;favorite=($i % 4 -eq 0);rating=($i % 6);added=([DateTime]::Now.AddMinutes(-$i).ToString('o'));category=(@('','landscape','portrait')[$i % 3]);taken='2000-01-01T00:00:00'}
}
$bad = Join-Path $images 'test-999-corrupt.jpg'
[IO.File]::WriteAllText($bad,'deliberately not an image')
$photos += @{path=$bad;favorite=$false;rating=0;added=([DateTime]::Now.AddDays(-2).ToString('o'));category='';taken=$null}
[IO.File]::WriteAllText((Join-Path $root 'library.json'),(@{version=1;photos=$photos}|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $root 'settings.json'),(@{FirstRun=$false;AutoCheckUpdates=$false;AutoScan=$false;ThemeMode=1;DisplayMode=1;FilmstripMode=0;AnimationsEnabled=$true;CardMinWidth=220}|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
$start = [Diagnostics.ProcessStartInfo]::new()
$testApp = Join-Path $root 'app'
New-Item -ItemType Directory -Path $testApp | Out-Null
Copy-Item -Path (Join-Path (Split-Path ([IO.Path]::GetFullPath($AppExe))) '*') -Destination $testApp -Recurse
$start.FileName = Join-Path $testApp 'PhotoAlbum.exe'
$start.UseShellExecute = $false
$start.Environment['HARUPHOTO_TEST_DATA'] = $root
if($PerformanceSmoke){$start.Environment['HARUPHOTO_PERFORMANCE_SMOKE']='1'}
$process = [Diagnostics.Process]::Start($start)
Write-Output ("QA profile: " + $root)
Write-Output ("QA process: " + $process.Id)
