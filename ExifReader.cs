using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace PhotoAlbum;

/// <summary>
/// EXIF 信息读取服务。从图片文件中读取拍摄日期、设备、分辨率等元数据。
/// 非打包模式下通过 StorageFile 直接访问文件系统。
/// </summary>
public static class ExifReader
{
    /// <summary>读取照片 EXIF 信息，返回人可读的多行文本；失败时返回 null。</summary>
    public static async Task<string?> LoadExifTextAsync(string filePath)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(filePath);
            var lines = new List<string>();
            uint width = 0, height = 0;

            // 基础图片属性和 EXIF 设备信息独立读取：相机信息缺失不能阻断分辨率等信息。
            try
            {
                var props = await file.Properties.GetImagePropertiesAsync();
                width = props.Width;
                height = props.Height;
                var camera = props.CameraManufacturer?.Trim() ?? "";
                var model = props.CameraModel?.Trim() ?? "";
                var device = camera.Length > 0 && model.Length > 0 ? $"{camera} {model}" : camera + model;
                if (device.Length > 0) lines.Add($"📷 设备：{device}");
            }
            catch { /* 常见于无 EXIF 或格式支持不完整的图片，继续走解码器兜底。 */ }

            try
            {
                using var stream = await file.OpenReadAsync();
                var decoder = await BitmapDecoder.CreateAsync(stream);
                width = width > 0 ? width : decoder.PixelWidth;
                height = height > 0 ? height : decoder.PixelHeight;

                var query = new[] { "System.Photo.ISOSpeed", "System.Photo.FNumber", "System.Photo.ExposureTime", "System.Photo.FocalLength" };
                var got = await decoder.BitmapProperties.GetPropertiesAsync(query);
                if (TryGetNumber(got, "System.Photo.ISOSpeed", out var iso)) lines.Add($"🎚 ISO：{Math.Round(iso):F0}");
                if (TryGetNumber(got, "System.Photo.FNumber", out var fnumber)) lines.Add($"🔘 光圈：f/{fnumber:F1}");
                if (TryGetNumber(got, "System.Photo.ExposureTime", out var exposure) && exposure > 0)
                {
                    lines.Add(exposure >= 1 ? $"⏱ 快门：{exposure:F1}s" : $"⏱ 快门：1/{Math.Round(1 / exposure)}s");
                }
                if (TryGetNumber(got, "System.Photo.FocalLength", out var focal)) lines.Add($"🔭 焦距：{focal:F0}mm");
            }
            catch { /* 解码失败时仍保留已读到的基础属性。 */ }

            var sourceModified = File.GetLastWriteTime(filePath);
            lines.Insert(0, $"📅 文件修改时间：{sourceModified:yyyy-MM-dd HH:mm:ss}");
            if (width > 0 && height > 0) lines.Insert(Math.Min(lines.Count, 2), $"🖼 分辨率：{width} × {height}");

            return lines.Count > 0 ? string.Join("\n", lines) : "（未检测到可读取的照片信息）";
        }
        catch
        {
            return "（无法读取这张照片的信息）";
        }
    }

    private static bool TryGetNumber(BitmapPropertySet properties, string key, out double number)
    {
        number = 0;
        if (!properties.TryGetValue(key, out var typed) || typed?.Value is null) return false;
        try
        {
            number = Convert.ToDouble(typed.Value, System.Globalization.CultureInfo.InvariantCulture);
            return !double.IsNaN(number) && !double.IsInfinity(number);
        }
        catch { return false; }
    }
}
