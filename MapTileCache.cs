using System.Net.Http;
namespace PhotoAlbum;

/// <summary>只请求浏览过的视口瓦片；旧缓存先显示、后台刷新，同一瓦片共用下载。</summary>
internal sealed class MapTileCache
{
    private static readonly HttpClient DefaultClient = CreateClient();
    private readonly HttpClient _client;
    private readonly string _directory;
    private readonly SemaphoreSlim _requests = new(4);
    private readonly object _gateLock = new();
    private readonly Dictionary<string, TileGate> _tileGates = new(StringComparer.Ordinal);
    private sealed class TileGate { public readonly SemaphoreSlim Semaphore = new(1); public int Users; }

    internal MapTileCache(HttpClient? client = null, string? directory = null)
    {
        _client = client ?? DefaultClient;
        _directory = directory ?? Path.Combine(AppPaths.DataDirectory, "map-tiles");
    }
    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("haruphoto/1.3.8 (+https://github.com/scj040921/haruphoto)");
        return client;
    }
    public Task<string?> GetAsync(int zoom, int x, int y, CancellationToken token)
    {
        var path = Path.Combine(_directory, $"{zoom}-{x}-{y}.png");
        token.ThrowIfCancellationRequested();
        if (File.Exists(path))
        {
            if (!IsFresh(path)) _ = RefreshAsync(zoom, x, y, path, token);
            return Task.FromResult<string?>(path);
        }
        return RefreshAsync(zoom, x, y, path, token);
    }

    private static bool IsFresh(string path) => File.Exists(path) && File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddDays(-7);

    private async Task<string?> RefreshAsync(int zoom, int x, int y, string path, CancellationToken token)
    {
        TileGate gate;
        lock (_gateLock)
        {
            if (!_tileGates.TryGetValue(path, out gate!)) _tileGates[path] = gate = new TileGate();
            gate.Users++;
        }
        var ownsGate = false; var ownsRequest = false; string? temporary = null;
        try
        {
            await gate.Semaphore.WaitAsync(token).ConfigureAwait(false); ownsGate = true;
            if (IsFresh(path)) return path;
            await _requests.WaitAsync(token).ConfigureAwait(false); ownsRequest = true;
            using var response = await _client.GetAsync($"https://tile.openstreetmap.org/{zoom}/{x}/{y}.png", HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 512 * 1024) return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
            if (bytes.Length > 512 * 1024 || bytes.Length < 8 || bytes[0] != 137 || bytes[1] != 80) return null;
            Directory.CreateDirectory(_directory);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temporary, bytes, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); File.Move(temporary, path, true);
            return path;
        }
        catch (OperationCanceledException) { return null; }
        catch { return File.Exists(path) ? path : null; }
        finally
        {
            if (temporary != null) { try { File.Delete(temporary); } catch { } }
            if (ownsRequest) _requests.Release();
            if (ownsGate) gate.Semaphore.Release();
            lock (_gateLock)
            {
                if (--gate.Users == 0) { _tileGates.Remove(path); gate.Semaphore.Dispose(); }
            }
        }
    }
    public static void PruneExpired()
    {
        var directory = Path.Combine(AppPaths.DataDirectory, "map-tiles");
        if (!Directory.Exists(directory)) return;
        try
        {
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.png", SearchOption.TopDirectoryOnly))
                if ((file.Attributes & FileAttributes.ReparsePoint) == 0 && file.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-30)
                    && System.Text.RegularExpressions.Regex.IsMatch(file.Name, @"^\d{1,2}-\d+-\d+\.png$")) file.Delete();
        }
        catch { }
    }
}
