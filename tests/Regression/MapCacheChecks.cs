using System.Net;
using PhotoAlbum;

internal static class MapCacheChecks
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        public int Count;
        public TaskCompletionSource<bool> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public byte[] Bytes = { 137, 80, 78, 71, 13, 10, 26, 10, 1 };
        public bool Fail;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Count);
            await Release.Task.WaitAsync(token);
            return Fail ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes) };
        }
    }
    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        if (!condition()) throw new Exception("Map cache test timed out");
    }
    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        using var handler = new FakeHandler(); using var client = new HttpClient(handler);
        var cache = new MapTileCache(client, Path.Combine(root, "map-cache-test"));
        var pending = Enumerable.Range(0, 12).Select(_ => cache.GetAsync(5, 10, 11, CancellationToken.None)).ToArray();
        await Until(() => Volatile.Read(ref handler.Count) == 1);
        handler.Release.SetResult(true); var paths = await Task.WhenAll(pending);
        check(paths.All(p => p != null && p == paths[0]) && handler.Count == 1, "concurrent requests share one map tile download");
        var path = paths[0]!;
        await cache.GetAsync(5, 10, 11, CancellationToken.None);
        check(handler.Count == 1, "fresh map tile uses local cache");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-8));
        handler.Bytes = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 2 };
        handler.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var stale = cache.GetAsync(5, 10, 11, CancellationToken.None);
        check(await Task.WhenAny(stale, Task.Delay(1000)) == stale && await stale == path, "expired map tile is shown immediately while refreshing");
        await Until(() => Volatile.Read(ref handler.Count) == 2);
        handler.Release.SetResult(true);
        await Until(() => File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddDays(-1));
        check((await File.ReadAllBytesAsync(path)).Last() == 2 && handler.Count == 2, "background refresh updates only the requested tile");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-8)); handler.Fail = true;
        await cache.GetAsync(5, 10, 11, CancellationToken.None);
        await Until(() => Volatile.Read(ref handler.Count) == 3);
        await Task.Delay(100);
        check(File.Exists(path) && (await File.ReadAllBytesAsync(path)).Last() == 2, "offline refresh preserves the cached tile");
    }
}
