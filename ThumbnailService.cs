using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
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

    public async Task<BitmapImage?> GetThumbnailAsync(string filePath, CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            var cachePath = GetCachePath(filePath);

            // 1. 缓存命中且比原图新 → 直接加载
            if (IsCacheValid(cachePath, filePath))
            {
                var hit = await LoadFromFileAsync(cachePath, ct);
                if (hit != null) return hit;
            }

            // 2. 解码原图 → 写缓存 → 从缓存加载
            if (await TryWriteCacheAsync(filePath, cachePath, ct))
            {
                var fromCache = await LoadFromFileAsync(cachePath, ct);
                if (fromCache != null) return fromCache;
            }

            // 3. 回退：直接解码原图(限宽)
            return await LoadFromFileAsync(filePath, ct);
        }
        catch (OperationCanceledException) { return null; }
        catch { return null; }
        finally { _semaphore.Release(); }
    }

    private static string GetCachePath(string filePath)
    {
        var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(filePath.ToLowerInvariant())));
        return Path.Combine(LibraryStore.ThumbnailCacheDir, key + ".jpg");
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
        try
        {
            ct.ThrowIfCancellationRequested();
            using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = await BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());

            double scale = Math.Min(1.0, ThumbWidth / (double)decoder.PixelWidth);
            var transform = new BitmapTransform
            {
                ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale)),
                ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale)),
                InterpolationMode = BitmapInterpolationMode.Fant,
            };

            var bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);

            ct.ThrowIfCancellationRequested();
            using var outStream = File.Create(cachePath);
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, outStream.AsRandomAccessStream());
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync();
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            try { if (File.Exists(cachePath)) File.Delete(cachePath); } catch { }
            return false;
        }
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
