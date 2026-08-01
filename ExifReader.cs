using System;
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
            var props = await file.Properties.GetImagePropertiesAsync();

            var dateTaken = props.DateTaken != DateTimeOffset.MinValue
                ? props.DateTaken.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss")
                : "";
            var camera = string.IsNullOrWhiteSpace(props.CameraManufacturer) ? "" : props.CameraManufacturer.Trim();
            var model = string.IsNullOrWhiteSpace(props.CameraModel) ? "" : props.CameraModel.Trim();
            var device = camera.Length > 0 && model.Length > 0 ? $"{camera} {model}" : (camera + model);

            // 详细 EXIF（ISO / 光圈 / 快门）
            string iso = "", fnumber = "", exposure = "", focal = "";
            try
            {
                using var stream = await file.OpenReadAsync();
                var decoder = await BitmapDecoder.CreateAsync(stream);
                var query = new[] { "System.Photo.ISOSpeed", "System.Photo.FNumber", "System.Photo.ExposureTime", "System.Photo.FocalLength" };
                var got = await decoder.BitmapProperties.GetPropertiesAsync(query);
                if (got.TryGetValue("System.Photo.ISOSpeed", out var v)) iso = $"{Convert.ToInt32(v.Value)}";
                if (got.TryGetValue("System.Photo.FNumber", out var f)) fnumber = $"f/{Convert.ToDouble(f.Value):F1}";
                if (got.TryGetValue("System.Photo.ExposureTime", out var e))
                {
                    var d = Convert.ToDouble(e.Value);
                    exposure = d >= 1 ? $"{d:F1}s" : $"1/{Math.Round(1 / d)}s";
                }
                if (got.TryGetValue("System.Photo.FocalLength", out var fl)) focal = $"{Convert.ToDouble(fl.Value):F0}mm";
            }
            catch { /* 无详细 EXIF 时忽略 */ }

            var w = props.Width > 0 ? props.Width : 0;
            var h = props.Height > 0 ? props.Height : 0;
            var lines = new System.Collections.Generic.List<string>();
            if (dateTaken.Length > 0) lines.Add($"📅 拍摄日期：{dateTaken}");
            if (device.Length > 0) lines.Add($"📷 设备：{device}");
            if (w > 0 && h > 0) lines.Add($"🖼 分辨率：{w} × {h}");
            if (iso.Length > 0) lines.Add($"🎚 ISO：{iso}");
            if (fnumber.Length > 0) lines.Add($"🔘 光圈：{fnumber}");
            if (exposure.Length > 0) lines.Add($"⏱ 快门：{exposure}");
            if (focal.Length > 0) lines.Add($"🔭 焦距：{focal}");

            return lines.Count > 0 ? string.Join("\n", lines) : null;
        }
        catch
        {
            return null;
        }
    }
}
