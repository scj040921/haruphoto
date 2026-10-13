using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PhotoAlbum;

public sealed record ReleaseInfo(string TagName, string Version, string PackageUrl, string Sha256, long PackageSize,
    string Notes = "", string Channel = "Stable", string Commit = "", bool UsePublicProxy = false)
{
    public string DisplayVersion => Channel == "Nightly" ? $"{Version} · {TagName}" : Version;
}
public sealed record DownloadedUpdate(string Directory, string PackagePath);

public static class UpdateService
{
    private const string RepoUrl = "https://github.com/scj040921/haruphoto";
    private const string ApiUrl = "https://api.github.com/repos/scj040921/haruphoto/releases";
    public const string PublicProxy = "https://gh-proxy.org/";
    private static readonly HttpClient Client = CreateClient();
    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("haruphoto-updater/1.3.3");
        return client;
    }
    public static string CurrentVersion => typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    public static BuildIdentity CurrentBuild { get; } = BuildIdentity.Load(AppContext.BaseDirectory, CurrentVersion);
    public static string CurrentDisplayVersion => $"{CurrentVersion} · {CurrentBuild.Channel}" + (CurrentBuild.Channel == "Nightly" ? $" · {CurrentBuild.Tag}" : "");
    public static string NormalizeChannel(string? channel) => channel == "Nightly" ? "Nightly" : "Stable";
    public static bool IsChannelTag(string tag, string channel) => Regex.IsMatch(tag, channel == "Nightly"
        ? "\\Anightly-[0-9]{8}-[0-9]+\\z" : "\\A[vV][0-9]+\\.[0-9]+\\.[0-9]+\\z");

    public static Task<ReleaseInfo?> CheckLatestAsync(CancellationToken cancellationToken = default)
        => CheckLatestAsync("Stable", false, cancellationToken);

    public static async Task<ReleaseInfo?> CheckLatestAsync(string channel, bool usePublicProxy, CancellationToken cancellationToken = default)
    {
        channel = NormalizeChannel(channel);
        try { return await CheckApiAsync(channel, usePublicProxy, cancellationToken); }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException)
        {
            return await CheckReleaseAssetsAsync(channel, usePublicProxy, cancellationToken);
        }
    }

    private static async Task<ReleaseInfo?> CheckApiAsync(string channel, bool usePublicProxy, CancellationToken cancellationToken)
    {
        channel = NormalizeChannel(channel);
        // 元数据只从 GitHub HTTPS API 获取，绝不将代理提供的哈希作为信任根。
        using var response = await Client.GetAsync(channel == "Stable" ? ApiUrl + "/latest" : ApiUrl + "?per_page=30", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        JsonElement selected = default;
        ReleaseInfo? release = null;
        var candidates = channel == "Stable" ? new[] { doc.RootElement } : doc.RootElement.EnumerateArray().ToArray();
        foreach (var candidate in candidates)
        {
            release = ParseRelease(candidate, CurrentVersion, channel, CurrentBuild.Channel, CurrentBuild.Tag);
            if (release == null) continue;
            selected = candidate;
            break;
        }
        if (release == null) return null;
        if (!IsSha256(release.Sha256))
        {
            var sums = selected.GetProperty("assets").EnumerateArray().FirstOrDefault(a => a.GetProperty("name").GetString() == "SHA256SUMS.txt");
            if (sums.ValueKind == JsonValueKind.Undefined || !sums.TryGetProperty("digest", out var digest) || !IsSha256(digest.GetString() ?? ""))
                throw new InvalidDataException("GitHub 未提供可信的包校验值，已停止更新。");
            var url = sums.GetProperty("browser_download_url").GetString() ?? "";
            ValidateAssetUrl(url, release.TagName, ".txt");
            var content = await ReadVerifiedTextAsync(url, digest.GetString()!, usePublicProxy, cancellationToken);
            release = release with { Sha256 = ReadChecksum(content, Path.GetFileName(new Uri(release.PackageUrl).AbsolutePath)) };
        }
        release = release with { UsePublicProxy = usePublicProxy };
        ValidateRelease(release);
        return release;
    }

    private static async Task<ReleaseInfo?> CheckReleaseAssetsAsync(string channel, bool usePublicProxy, CancellationToken token)
    {
        var expectedTag = "";
        string url;
        if (channel == "Stable") url = RepoUrl + "/releases/latest/download/update.json";
        else
        {
            var xml = await Client.GetStringAsync(RepoUrl + "/releases.atom", token);
            var feed = XDocument.Parse(xml);
            XNamespace atom = "http://www.w3.org/2005/Atom";
            var tags = feed.Descendants(atom + "entry").Elements(atom + "link")
                .Select(e => (string?)e.Attribute("href") ?? "")
                .Where(link => link.StartsWith(RepoUrl + "/releases/tag/", StringComparison.Ordinal))
                .Select(link => Uri.UnescapeDataString(link[(RepoUrl + "/releases/tag/").Length..]))
                .Where(tag => IsChannelTag(tag, "Nightly")).ToList();
            if (tags.Count == 0) return null;
            tags.Sort(CompareNightlyTags);
            expectedTag = tags[^1];
            url = RepoUrl + "/releases/download/" + expectedTag + "/update.json";
        }
        // 备用清单也是本仓库的 Release 资产，直接经 GitHub HTTPS 下载，不经第三方代理。
        using var response = await Client.GetAsync(url, token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var result = ParseChannelManifest(doc.RootElement, CurrentVersion, channel, CurrentBuild.Channel, CurrentBuild.Tag);
        if (result != null && expectedTag.Length > 0 && result.TagName != expectedTag) throw new InvalidDataException("Release 标签与清单不一致。");
        return result == null ? null : result with { UsePublicProxy = usePublicProxy };
    }

    internal static ReleaseInfo? ParseChannelManifest(JsonElement root, string currentVersion, string channel, string currentChannel, string currentTag)
    {
        var tag = root.GetProperty("tag").GetString() ?? "";
        var version = root.GetProperty("version").GetString() ?? "";
        var declaredChannel = root.TryGetProperty("channel", out var ch) ? ch.GetString() : "Stable";
        if (declaredChannel != channel || !IsChannelTag(tag, channel) || root.GetProperty("architecture").GetString() != "win-x64") throw new InvalidDataException("Release 清单通道或架构不匹配。");
        if (channel == "Stable")
        {
            if (version != tag[1..]) throw new InvalidDataException("Release 清单版本不匹配。");
            if (currentChannel == "Stable" && !IsNewer(tag, currentVersion, out _)) return null;
        }
        else if (currentChannel == "Nightly" && (CompareNightlyTags(tag, currentTag) <= 0 || System.Version.Parse(version) < System.Version.Parse(currentVersion))) return null;
        var release = new ReleaseInfo(tag, version, root.GetProperty("url").GetString() ?? "", root.GetProperty("sha256").GetString() ?? "", root.GetProperty("size").GetInt64(),
            root.TryGetProperty("notes", out var notes) ? notes.GetString() ?? "" : "", channel, root.TryGetProperty("commit", out var commit) ? commit.GetString() ?? "" : "");
        ValidateRelease(release);
        return release;
    }

    internal static ReleaseInfo? ParseRelease(JsonElement root, string currentVersion, string channel = "Stable", string currentChannel = "Stable", string currentTag = "")
    {
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        var preview = root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean();
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!IsChannelTag(tag, channel) || preview != (channel == "Nightly")) return null;
        var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        string version;
        if (channel == "Stable")
        {
            if (!IsNewer(tag, currentVersion, out version) && currentChannel == "Stable") return null;
            version = tag[1..];
        }
        else
        {
            var match = Regex.Match(notes, @"haruphoto-version:(?<version>\d+\.\d+\.\d+)");
            if (!match.Success) return null;
            version = match.Groups["version"].Value;
            if (currentChannel == "Nightly" && System.Version.Parse(version) < System.Version.Parse(currentVersion)) return null;
            if (currentChannel == "Nightly" && CompareNightlyTags(tag, currentTag) <= 0) return null;
        }
        ReleaseInfo? legacy = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (!name.StartsWith("haruphoto-", StringComparison.OrdinalIgnoreCase) || (!name.EndsWith("-portable.zip", StringComparison.OrdinalIgnoreCase) && !name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase))) continue;
            if (name.Contains("arm64", StringComparison.OrdinalIgnoreCase) || name.Contains("x86", StringComparison.OrdinalIgnoreCase)) continue;
            var candidate = new ReleaseInfo(tag, version, asset.GetProperty("browser_download_url").GetString() ?? "",
                asset.TryGetProperty("digest", out var digest) ? digest.GetString() ?? "" : "",
                asset.TryGetProperty("size", out var size) ? size.GetInt64() : 0, notes, channel,
                root.TryGetProperty("target_commitish", out var commit) ? commit.GetString() ?? "" : "");
            if (name.EndsWith("-portable.zip", StringComparison.OrdinalIgnoreCase)) return candidate;
            legacy = candidate;
        }
        // 发布尚未完成时不使用源码包或安装器冒充更新 ZIP。
        return legacy;
    }

    internal static int CompareNightlyTags(string left, string right)
    {
        if (!IsChannelTag(right, "Nightly")) return 1;
        var a = left.Split('-'); var b = right.Split('-');
        var date = string.CompareOrdinal(a[1], b[1]);
        return date != 0 ? date : System.Numerics.BigInteger.Parse(a[2]).CompareTo(System.Numerics.BigInteger.Parse(b[2]));
    }

    // 保留旧清单解析供旧版兼容测试；新客户端的发现入口只读取 GitHub Releases。
    internal static ReleaseInfo? ParseManifest(JsonElement root, string currentVersion)
    {
        var tag = root.GetProperty("tag").GetString() ?? "";
        if (!IsNewer(tag, currentVersion, out var version)) return null;
        if (root.GetProperty("version").GetString() != version || root.GetProperty("architecture").GetString() != "win-x64") throw new InvalidDataException("更新清单版本或架构不匹配。");
        var release = new ReleaseInfo(tag, version, root.GetProperty("url").GetString() ?? "", root.GetProperty("sha256").GetString() ?? "", root.GetProperty("size").GetInt64());
        ValidateRelease(release);
        return release;
    }
    internal static bool IsNewer(string tag, string current, out string version)
    {
        version = "";
        if (!IsChannelTag(tag, "Stable") || !System.Version.TryParse(tag[1..], out var remote) || !System.Version.TryParse(current, out var local)) return false;
        version = remote.ToString(3); return remote > local;
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
    private static void ValidateAssetUrl(string url, string tag, string extension)
    {
        var prefix = RepoUrl + "/releases/download/" + Uri.EscapeDataString(tag) + "/";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !url.StartsWith(prefix, StringComparison.Ordinal)
            || uri.Query.Length != 0 || uri.Fragment.Length != 0 || !uri.AbsolutePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            || uri.AbsoluteUri != url || !Regex.IsMatch(url[prefix.Length..], "\\A[a-zA-Z0-9_.-]+\\z") || url[prefix.Length..].Contains(".."))
            throw new InvalidDataException("更新来源无效，已停止更新。");
    }
    internal static void ValidateRelease(ReleaseInfo release)
    {
        if (!IsChannelTag(release.TagName, release.Channel) || !System.Version.TryParse(release.Version, out var version) || version.Build < 0 || version.Revision >= 0 || !IsSha256(release.Sha256))
            throw new InvalidDataException("更新版本或校验信息无效，已停止更新。");
        ValidateAssetUrl(release.PackageUrl, release.TagName, ".zip");
    }
    internal static string ProxyUrl(string url)
    {
        // 代理只接受本仓库的公开 Release 资产，不接受 API、令牌或任意 URL。
        if (!url.StartsWith(RepoUrl + "/releases/download/", StringComparison.Ordinal)) throw new InvalidDataException("禁止代理此地址。");
        return PublicProxy + url;
    }
    private static async Task<string> ReadVerifiedTextAsync(string url, string expected, bool proxy, CancellationToken token)
    {
        foreach (var endpoint in proxy ? new[] { url, ProxyUrl(url) } : new[] { url })
        {
            try
            {
                using var response = await Client.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > 256 * 1024) throw new InvalidDataException("校验清单过大。");
                await using var source = await response.Content.ReadAsStreamAsync(token);
                using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
                while ((count = await source.ReadAsync(buffer, token)) > 0)
                {
                    if (output.Length + count > 256 * 1024) throw new InvalidDataException("校验清单过大。");
                    output.Write(buffer, 0, count);
                }
                var bytes = output.ToArray();
                if (bytes.Length > 256 * 1024 || !HashMatches(bytes, expected)) throw new InvalidDataException("校验清单被篡改或过大。");
                return Encoding.UTF8.GetString(bytes);
            }
            catch (Exception ex) when (!token.IsCancellationRequested && endpoint == url && proxy && Retryable(ex)) { }
        }
        throw new IOException("无法读取可信校验清单。");
    }
    private static bool HashMatches(byte[] bytes, string hash) => Convert.ToHexString(SHA256.HashData(bytes)).Equals(hash.Replace("sha256:", "", StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase);
    private static bool Retryable(Exception ex) => ex is HttpRequestException or IOException or OperationCanceledException;

    public static Task<DownloadedUpdate> DownloadAsync(ReleaseInfo release, IProgress<double>? progress, CancellationToken token)
        => DownloadUsingClientAsync(release, progress, token, Client);
    internal static async Task<DownloadedUpdate> DownloadUsingClientAsync(ReleaseInfo release, IProgress<double>? progress, CancellationToken token, HttpClient client)
    {
        ValidateRelease(release);
        var directory = Path.Combine(Path.GetTempPath(), "haruphoto-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "package.zip");
        try
        {
            using var overall = CancellationTokenSource.CreateLinkedTokenSource(token);
            overall.CancelAfter(TimeSpan.FromMinutes(15));
            foreach (var endpoint in release.UsePublicProxy ? new[] { release.PackageUrl, ProxyUrl(release.PackageUrl) } : new[] { release.PackageUrl })
            {
                try
                {
                    progress?.Report(0);
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
                    idle.CancelAfter(TimeSpan.FromSeconds(20));
                    using var response = await client.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, idle.Token);
                    response.EnsureSuccessStatusCode();
                    var headerSize = response.Content.Headers.ContentLength;
                    if (release.PackageSize > 0 && headerSize.HasValue && headerSize != release.PackageSize) throw new InvalidDataException("下载包大小与 GitHub 不一致。");
                    var total = release.PackageSize > 0 ? release.PackageSize : headerSize ?? 0;
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    await using (var source = await response.Content.ReadAsStreamAsync(idle.Token))
                    await using (var destination = File.Create(path))
                    {
                        var buffer = new byte[81920]; long received = 0; int length;
                        while (true)
                        {
                            idle.CancelAfter(TimeSpan.FromSeconds(45));
                            length = await source.ReadAsync(buffer, idle.Token);
                            if (length == 0) break;
                            await destination.WriteAsync(buffer.AsMemory(0, length), idle.Token);
                            hash.AppendData(buffer, 0, length); received += length;
                            if (total > 0 && received > total) throw new InvalidDataException("下载包超出预期大小。");
                            progress?.Report(total > 0 ? Math.Min(100, 100.0 * received / total) : -1);
                        }
                        if (total > 0 && received != total) throw new InvalidDataException("下载包不完整。");
                    }
                    if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256.Replace("sha256:", "", StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SHA256 校验失败，旧版本未被修改。");
                    return new(directory, path);
                }
                catch (Exception ex) when (!overall.IsCancellationRequested && endpoint == release.PackageUrl && release.UsePublicProxy && Retryable(ex)) { }
            }
            throw new IOException("无法下载可信更新包。");
        }
        catch { CleanupDownload(directory); throw; }
    }
    public static string SaveUpdaterScript(string directory)
    {
        using var stream = typeof(UpdateService).Assembly.GetManifestResourceStream("PhotoAlbum.update.ps1") ?? throw new FileNotFoundException("缺少更新器。");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var path = Path.Combine(directory, "update.ps1");
        File.WriteAllText(path, reader.ReadToEnd(), new UTF8Encoding(true)); return path;
    }
    public static void CleanupDownload(string directory)
    {
        var path = Path.GetFullPath(directory); var prefix = Path.Combine(Path.GetTempPath(), "haruphoto-update-");
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(path[prefix.Length..], "N", out _)) return;
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }
}
