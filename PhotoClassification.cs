using System.Globalization;
namespace PhotoAlbum;

internal static class PhotoClassification
{
    public static string Suggest(PhotoItem photo, int rule, string keyword = "", string category = "", double? aspect = null)
        => rule switch
        {
            0 => photo.TimelineDate.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            1 => Path.GetFileName(Path.GetDirectoryName(photo.FilePath)) ?? "",
            2 => Path.GetExtension(photo.FilePath).TrimStart('.').ToUpperInvariant(),
            3 => aspect is not > 0 ? "" : aspect > 1.05 ? "横向照片" : aspect < 0.95 ? "竖向照片" : "方形照片",
            4 => !string.IsNullOrWhiteSpace(keyword) && !string.IsNullOrWhiteSpace(category)
                && photo.Filename.Contains(keyword.Trim(), StringComparison.OrdinalIgnoreCase) ? category.Trim() : "",
            _ => ""
        };
}
