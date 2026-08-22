using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PhotoAlbum;

public sealed record ReleaseInfo(string TagName, string Version, string PackageUrl, string Sha256, long PackageSize);

public static class UpdateService
{
    private const string LatestApi = "https://api.github.com/repos/scj040921/haruphoto/releases/latest";
    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("haruphoto-updater/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static string CurrentVersion =>
        typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static async Task<ReleaseInfo?> CheckLatestAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await Client.GetAsync(LatestApi, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return ParseRelease(doc.RootElement);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            // GitHub's unauthenticated API is rate-limited. The public release page
            // redirects without API quota, so use it as a read-only fallback.
            return await CheckLatestFromReleasePageAsync(cancellationToken);
        }
    }

    private static ReleaseInfo? ParseRelease(JsonElement root)
    {
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!TryGetNewerVersion(tag, out var remoteVersion)) return null;

        ReleaseInfo? fallback = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
            var url = asset.GetProperty("browser_download_url").GetString() ?? "";
            var digest = asset.TryGetProperty("digest", out var digestElement) ? digestElement.GetString() ?? "" : "";
            var size = asset.TryGetProperty("size", out var sizeElement) ? sizeElement.GetInt64() : 0;
            if (string.IsNullOrWhiteSpace(url)) continue;
            var candidate = new ReleaseInfo(tag, remoteVersion.ToString(3), url, digest, size);
            if (name.EndsWith("-portable.zip", StringComparison.OrdinalIgnoreCase)) return candidate;
            fallback ??= candidate;
        }
        return fallback;
    }

    private static bool TryGetNewerVersion(string tag, out Version remoteVersion)
    {
        remoteVersion = new Version();
        var versionText = tag.Trim().TrimStart('v', 'V');
        if (!Version.TryParse(versionText, out var parsed)) return false;
        if (!Version.TryParse(CurrentVersion, out var localVersion) || parsed <= localVersion) return false;
        remoteVersion = parsed;
        return true;
    }

    private static async Task<ReleaseInfo?> CheckLatestFromReleasePageAsync(CancellationToken cancellationToken)
    {
        using var response = await Client.GetAsync("https://github.com/scj040921/haruphoto/releases/latest", cancellationToken);
        response.EnsureSuccessStatusCode();
        var finalUrl = response.RequestMessage?.RequestUri?.AbsoluteUri ?? "";
        var match = Regex.Match(finalUrl, @"/releases/tag/(?<tag>[^/?#]+)$", RegexOptions.IgnoreCase);
        var tag = match.Success ? Uri.UnescapeDataString(match.Groups["tag"].Value) : "";
        if (!TryGetNewerVersion(tag, out var remoteVersion)) return null;

        var assetName = $"haruphoto-v{remoteVersion:0.0.0}-portable.zip";
        var packageUrl = $"https://github.com/scj040921/haruphoto/releases/download/{Uri.EscapeDataString(tag)}/{assetName}";
        return new ReleaseInfo(tag, remoteVersion.ToString(3), packageUrl, "", 0);
    }

    public static string SaveUpdaterScript()
    {
        var installedScript = Path.Combine(AppContext.BaseDirectory, "tools", "update.ps1");
        if (File.Exists(installedScript)) return installedScript;
        var path = Path.Combine(Path.GetTempPath(), "haruphoto-update.ps1");
        File.WriteAllText(path, EmbeddedUpdaterScript);
        return path;
    }

    private const string EmbeddedUpdaterScript = @"
param([Parameter(Mandatory=$true)][int]$ProcessId,[Parameter(Mandatory=$true)][string]$InstallRoot,[Parameter(Mandatory=$true)][string]$PackageUrl,[Parameter(Mandatory=$true)][string]$ExpectedSha256,[Parameter(Mandatory=$true)][string]$Version)
$ErrorActionPreference='Stop'
$temp=Join-Path $env:TEMP ('haruphoto-update-'+[Guid]::NewGuid().ToString('N')); $zip=Join-Path $temp 'package.zip'; $stage=Join-Path $temp 'stage'; $backup=$InstallRoot+'.backup-'+$Version
function Restore { if(Test-Path $backup){if(Test-Path $InstallRoot){Remove-Item $InstallRoot -Recurse -Force -ErrorAction SilentlyContinue};Move-Item $backup $InstallRoot -Force} }
try { New-Item -ItemType Directory -Force -Path $stage | Out-Null; Invoke-WebRequest $PackageUrl -OutFile $zip -UseBasicParsing; $expected=$ExpectedSha256.ToLower().Replace('sha256:',''); if($expected){$actual=(Get-FileHash $zip -Algorithm SHA256).Hash.ToLower(); if($actual -ne $expected){throw 'SHA256 校验失败'}}; Expand-Archive $zip $stage -Force; $payload=(Get-ChildItem $stage -Filter 'PhotoAlbum.exe' -Recurse -File | Select-Object -First 1).Directory.FullName; $p=Get-Process -Id $ProcessId -ErrorAction SilentlyContinue; if($p){Wait-Process -Id $ProcessId -Timeout 45 -ErrorAction SilentlyContinue}; if(Get-Process -Id $ProcessId -ErrorAction SilentlyContinue){throw '等待程序退出超时'}; if(Test-Path $backup){Remove-Item $backup -Recurse -Force}; Copy-Item $InstallRoot $backup -Recurse -Force; Copy-Item (Join-Path $payload '*') $InstallRoot -Recurse -Force; Start-Process (Join-Path $InstallRoot 'PhotoAlbum.exe'); Remove-Item $backup -Recurse -Force -ErrorAction SilentlyContinue } catch { Restore; Add-Type -AssemblyName PresentationFramework; [System.Windows.MessageBox]::Show(('更新失败，已恢复旧版本。'+[Environment]::NewLine+$_.Exception.Message),'haruphoto 更新') | Out-Null } finally { Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue }
";
}
