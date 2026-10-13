using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace PhotoAlbum;

/// <summary>
/// 缩略图管线：
/// 1. 命中磁盘缓存则直接加载(毫秒级)；
/// 2. 否则用 BitmapDecoder 按目标宽度解码原图(修复旧代码先设源后设 DecodePixelWidth 导致全分辨率解码的内存问题)，
///    编码为 JPEG 写入 %LocalAppData%\haruphoto\thumbnails，再加载显示；
/// 3. 全部失败时回退到 BitmapImage 直接解码(限宽)。
/// 通过信号量限制并发，避免大量照片同时解码占满内存。
/// </summary>
public sealed class ThumbnailService
{
    private const int ThumbWidth = 400;
    private readonly SemaphoreSlim _semaphore = new(4);

    // 固定数量的分片锁，避免每浏览一个文件就永久保留一个 SemaphoreSlim。
    private readonly SemaphoreSlim[] _fileLocks = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1)).ToArray();
    private int _cacheWrites;
    public long CacheBudgetBytes { get; set; } = 128L * 1024 * 1024;

    public async Task<BitmapImage?> GetThumbnailAsync(string filePath, CancellationToken ct = default)
    {
        // 图库与胶片条可能同时请求同一张照片；同一路径串行写缓存。
        var fileLock = _fileLocks[(uint)StringComparer.OrdinalIgnoreCase.GetHashCode(filePath) % (uint)_fileLocks.Length];
        await fileLock.WaitAsync(ct);
        try
        {
            await _semaphore.WaitAsync(ct);
            try
            {
                var cachePath = ThumbnailCacheMaintenance.CachePath(filePath, LibraryStore.ThumbnailCacheDir);
                if (IsCacheValid(cachePath, filePath))
                {
                    var hit = await LoadFromFileAsync(cachePath, ct);
                    if (hit != null) return hit;
                }
                if (await TryWriteCacheAsync(filePath, cachePath, ct))
                {
                    var cached = await LoadFromFileAsync(cachePath, ct);
                    if (Interlocked.Increment(ref _cacheWrites) % 64 == 0) _ = MaintainCacheAsync();
                    if (cached != null) return cached;
                }
                return await LoadFromFileAsync(filePath, ct);
            }
            catch (OperationCanceledException) { return null; }
            catch { return null; }
            finally { _semaphore.Release(); }
        }
        finally { fileLock.Release(); }
    }

    private async Task MaintainCacheAsync()
    {
        try { await ThumbnailCacheMaintenance.TrimAsync(LibraryStore.ThumbnailCacheDir, CacheBudgetBytes); }
        catch { /* 缓存是可重建数据；被占用时下次维护再处理。 */ }
    }

    private static bool IsCacheValid(string cachePath, string sourcePath)
    {
        try
        {
            return File.Exists(cachePath)
                && File.GetLastWriteTimeUtc(cachePath) >= File.GetLastWriteTimeUtc(sourcePath);
        }
        catch { return false; }
    }

    /// <summary>解码原图并按比例缩放到 400px 宽，编码为 JPEG 写入缓存文件。</summary>
    private static async Task<bool> TryWriteCacheAsync(string filePath, string cachePath, CancellationToken ct)
    {
        var temporary = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            ct.ThrowIfCancellationRequested();
            using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = await BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());

            double scale = Math.Min(1.0, Math.Min(ThumbWidth / (double)decoder.PixelWidth, 1200d / decoder.PixelHeight));
            var transform = new BitmapTransform
            {
                ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale)),
                ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale)),
                InterpolationMode = BitmapInterpolationMode.Fant,
            };

            using var bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);

            ct.ThrowIfCancellationRequested();
            using (var outStream = File.Create(temporary))
            {
                var properties = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(0.78f, Windows.Foundation.PropertyType.Single) };
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, outStream.AsRandomAccessStream(), properties);
                encoder.SetSoftwareBitmap(bitmap);
                await encoder.FlushAsync();
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, cachePath, overwrite: true);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return false;
        }
        finally { try { File.Delete(temporary); } catch { } }
    }

    private static async Task<BitmapImage?> LoadFromFileAsync(string path, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            // 关键修复：先设置 DecodePixelWidth，再加载源，解码直接在目标尺寸进行
            var bmp = new BitmapImage { DecodePixelWidth = ThumbWidth };
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using IRandomAccessStream ras = stream.AsRandomAccessStream();
            await bmp.SetSourceAsync(ras);
            return bmp;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }
}
