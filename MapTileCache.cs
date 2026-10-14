using System.Net.Http;
namespace PhotoAlbum;

/// <summary>只请求当前视口，稳定应用标识，至少缓存七天；不预取、不提供离线下载。</summary>
internal sealed class MapTileCache
{
    private static readonly HttpClient Client = CreateClient();
    private readonly SemaphoreSlim _requests = new(4);
    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("haruphoto/1.3.8 (+https://github.com/scj040921/haruphoto)");
        return client;
    }
    public async Task<string?> GetAsync(int zoom, int x, int y, CancellationToken token)
    {
        var directory = Path.Combine(AppPaths.DataDirectory, "map-tiles");
        var path = Path.Combine(directory, $"{zoom}-{x}-{y}.png");
        if (File.Exists(path) && File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddDays(-7)) return path;
        await _requests.WaitAsync(token);
        string? temporary = null;
        try
        {
            token.ThrowIfCancellationRequested();
            if (File.Exists(path) && File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddDays(-7)) return path;
            using var response = await Client.GetAsync($"https://tile.openstreetmap.org/{zoom}/{x}/{y}.png", HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 512 * 1024) return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(token);
            if (bytes.Length > 512 * 1024 || bytes.Length < 8 || bytes[0] != 137 || bytes[1] != 80) return null;
            Directory.CreateDirectory(directory);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temporary, bytes, token);
            token.ThrowIfCancellationRequested(); File.Move(temporary, path, true);
            return path;
        }
        catch (OperationCanceledException) { return null; }
        catch { return File.Exists(path) ? path : null; }
        finally { if (temporary != null) { try { File.Delete(temporary); } catch { } } _requests.Release(); }
    }
    public static void PruneExpired()
    {
        var directory = Path.Combine(AppPaths.DataDirectory, "map-tiles");
        if (!Directory.Exists(directory)) return;
        try
        {
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.png", SearchOption.TopDirectoryOnly))
                if ((file.Attributes & FileAttributes.ReparsePoint) == 0 && file.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-7)
                    && System.Text.RegularExpressions.Regex.IsMatch(file.Name, @"^\d{1,2}-\d+-\d+\.png$")) file.Delete();
        }
        catch { }
    }
}
