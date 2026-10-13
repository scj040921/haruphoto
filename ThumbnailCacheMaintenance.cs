using System.Security.Cryptography;
using System.Text;

namespace PhotoAlbum;

internal sealed record CacheUsage(int Count, long Bytes);
internal sealed record CacheCleanup(int Removed, long FreedBytes, CacheUsage Remaining);

internal static class ThumbnailCacheMaintenance
{
    private static readonly SemaphoreSlim Gate = new(1);
    public static string CachePath(string source, string directory) => Path.Combine(directory,
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(source.ToLowerInvariant()))) + ".jpg");

    private static List<FileInfo> Files(string directory) => !Directory.Exists(directory) ? new() :
        Directory.EnumerateFiles(directory, "*.jpg", SearchOption.TopDirectoryOnly)
            .Where(p => Path.GetFileNameWithoutExtension(p) is var name && name.Length == 40 && name.All(Uri.IsHexDigit))
            .Select(p => new FileInfo(p)).Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0).ToList();

    public static CacheUsage Usage(string directory)
    {
        var files = Files(directory);
        return new CacheUsage(files.Count, files.Sum(f => f.Length));
    }

    public static async Task<CacheCleanup> TrimAsync(string directory, long budgetBytes, CancellationToken token = default)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var files = Files(directory).OrderBy(f => f.LastWriteTimeUtc).ToList();
                var total = files.Sum(f => f.Length); var removed = 0; long freed = 0;
                foreach (var file in files)
                {
                    token.ThrowIfCancellationRequested();
                    if (total <= Math.Max(0, budgetBytes)) break;
                    try { var size = file.Length; file.Delete(); total -= size; freed += size; removed++; }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                return new CacheCleanup(removed, freed, new CacheUsage(files.Count - removed, total));
            }, token).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }
}
