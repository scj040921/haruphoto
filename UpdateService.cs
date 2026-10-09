using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhotoAlbum;

public sealed record ReleaseInfo(string TagName, string Version, string PackageUrl, string Sha256, long PackageSize, string Notes = "");
public sealed record DownloadedUpdate(string Directory, string PackagePath);

public static class UpdateService
{
    private const string RepoUrl = "https://github.com/scj040921/haruphoto";
    private static readonly HttpClient Client = CreateClient();
    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("haruphoto-updater/1.3.2");
        return client;
    }
    public static string CurrentVersion => typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static async Task<ReleaseInfo?> CheckLatestAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await Client.GetAsync("https://api.github.com/repos/scj040921/haruphoto/releases/latest", cancellationToken);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var release = ParseRelease(doc.RootElement, CurrentVersion);
            if (release == null) return null;
            if (!IsSha256(release.Sha256))
            {
                var sums = await Client.GetStringAsync($"{RepoUrl}/releases/download/{Uri.EscapeDataString(release.TagName)}/SHA256SUMS.txt", cancellationToken);
                release = release with { Sha256 = ReadChecksum(sums, Path.GetFileName(new Uri(release.PackageUrl).AbsolutePath)) };
            }
            ValidateRelease(release);
            return release;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && (ex is HttpRequestException || ex is TaskCanceledException))
        {
            var json = await Client.GetStringAsync(RepoUrl + "/releases/latest/download/update.json", cancellationToken);
            using var doc = JsonDocument.Parse(json);
            return ParseManifest(doc.RootElement, CurrentVersion);
        }
    }

    internal static ReleaseInfo? ParseRelease(JsonElement root, string currentVersion)
    {
        if ((root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) || (root.TryGetProperty("prerelease", out var preview) && preview.GetBoolean())) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!IsNewer(tag, currentVersion, out var version)) return null;
        ReleaseInfo? legacy = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (!name.EndsWith("-portable.zip", StringComparison.OrdinalIgnoreCase) && !name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Contains("arm64", StringComparison.OrdinalIgnoreCase) || name.Contains("x86", StringComparison.OrdinalIgnoreCase)) continue;
            var candidate = new ReleaseInfo(tag, version, asset.GetProperty("browser_download_url").GetString() ?? "",
                asset.TryGetProperty("digest", out var digest) ? digest.GetString() ?? "" : "",
                asset.TryGetProperty("size", out var size) ? size.GetInt64() : 0,
                root.TryGetProperty("body", out var notes) ? notes.GetString() ?? "" : "");
            if (name.EndsWith("-portable.zip", StringComparison.OrdinalIgnoreCase)) return candidate;
            legacy = candidate;
        }
        return legacy ?? throw new InvalidDataException("新版本尚未提供 Windows x64 更新包，请稍后重试。");
    }

    internal static ReleaseInfo? ParseManifest(JsonElement root, string currentVersion)
    {
        var tag = root.GetProperty("tag").GetString() ?? "";
        if (!IsNewer(tag, currentVersion, out var version)) return null;
        if (root.GetProperty("version").GetString() != version || root.GetProperty("architecture").GetString() != "win-x64") throw new InvalidDataException("更新清单版本或架构不匹配。");
        var release = new ReleaseInfo(tag, version, root.GetProperty("url").GetString() ?? "", root.GetProperty("sha256").GetString() ?? "", root.GetProperty("size").GetInt64(),
            root.TryGetProperty("notes", out var notes) ? notes.GetString() ?? "" : "");
        ValidateRelease(release);
        return release;
    }

    internal static bool IsNewer(string tag, string current, out string version)
    {
        version = "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var remote) || remote.Build < 0 || !Version.TryParse(current, out var local)) return false;
        version = remote.ToString(3);
        return remote > local;
    }
    internal static bool IsSha256(string digest) => Regex.IsMatch(digest.Replace("sha256:", "", StringComparison.OrdinalIgnoreCase), "\\A[0-9a-fA-F]{64}\\z");
    internal static string ReadChecksum(string contents, string fileName)
    {
        foreach (var line in contents.Split('\n'))
        {
            var match = Regex.Match(line.Trim(), @"^(?<hash>[a-fA-F0-9]{64})\s+\*?(?<name>.+)$");
            if (match.Success && match.Groups["name"].Value == fileName) return match.Groups["hash"].Value;
        }
        throw new InvalidDataException("发布包缺少 SHA256 校验值，已停止更新。");
    }
    internal static void ValidateRelease(ReleaseInfo release)
    {
        var prefix = RepoUrl + "/releases/download/" + Uri.EscapeDataString(release.TagName) + "/";
        if (!release.PackageUrl.StartsWith(prefix, StringComparison.Ordinal) || !release.PackageUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || !IsSha256(release.Sha256))
            throw new InvalidDataException("更新来源或校验信息无效，已停止更新。");
    }

    public static async Task<DownloadedUpdate> DownloadAsync(ReleaseInfo release, IProgress<double>? progress, CancellationToken cancellationToken)
        => await DownloadUsingClientAsync(release, progress, cancellationToken, Client);

    internal static async Task<DownloadedUpdate> DownloadUsingClientAsync(ReleaseInfo release, IProgress<double>? progress, CancellationToken cancellationToken, HttpClient client)
    {
        ValidateRelease(release);
        var directory = Path.Combine(Path.GetTempPath(), "haruphoto-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "package.zip");
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(15));
            using var response = await client.GetAsync(release.PackageUrl, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? release.PackageSize;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var source = await response.Content.ReadAsStreamAsync(deadline.Token))
            await using (var destination = File.Create(path))
            {
                var buffer = new byte[81920]; long received = 0; int length;
                while ((length = await source.ReadAsync(buffer, deadline.Token)) != 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, length), deadline.Token);
                    hash.AppendData(buffer, 0, length); received += length;
                    progress?.Report(total > 0 ? Math.Min(100, 100.0 * received / total) : -1);
                }
                if (total > 0 && received != total) throw new InvalidDataException("下载包不完整，请重新尝试。");
            }
            if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256.Replace("sha256:", "", StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SHA256 校验失败，应用未关闭，旧版本未被修改。");
            return new DownloadedUpdate(directory, path);
        }
        catch { CleanupDownload(directory); throw; }
    }

    public static string SaveUpdaterScript(string directory)
    {
        using var stream = typeof(UpdateService).Assembly.GetManifestResourceStream("PhotoAlbum.update.ps1") ?? throw new FileNotFoundException("缺少更新器。");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var path = Path.Combine(directory, "update.ps1");
        File.WriteAllText(path, reader.ReadToEnd(), new UTF8Encoding(true));
        return path;
    }
    public static void CleanupDownload(string directory)
    {
        var path = Path.GetFullPath(directory);
        var prefix = Path.Combine(Path.GetTempPath(), "haruphoto-update-");
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(path[prefix.Length..], "N", out _)) return;
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }
}
