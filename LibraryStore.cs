using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace PhotoAlbum;

/// <summary>
/// 照片库持久化：将照片元数据(路径/收藏/评分/添加时间)以 JSON 保存在
/// %LocalAppData%\haruphoto\library.json，启动时自动恢复。
/// </summary>
public static class LibraryStore
{
    private sealed class Entry
    {
        [JsonPropertyName("path")] public string Path { get; set; } = "";
        [JsonPropertyName("favorite")] public bool Favorite { get; set; }
        [JsonPropertyName("rating")] public int Rating { get; set; }
        [JsonPropertyName("added")] public DateTime Added { get; set; }
        [JsonPropertyName("category")] public string Category { get; set; } = "";
        [JsonPropertyName("taken")] public DateTime? Taken { get; set; }
    }

    private sealed class LibraryFile
    {
        [JsonPropertyName("version")] public int Version { get; set; } = 1;
        [JsonPropertyName("photos")] public List<Entry> Photos { get; set; } = new();
    }

    private static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "haruphoto");

    public static string LibraryPath => Path.Combine(DataDir, "library.json");
    public static string ThumbnailCacheDir => Path.Combine(DataDir, "thumbnails");

    static LibraryStore()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(ThumbnailCacheDir);
    }

    /// <summary>
    /// 加载照片库。返回 (照片列表, 文件已缺失被跳过的数量)。
    /// 磁盘上不存在的文件会被跳过，但记录仍保留在 JSON 中(可能是可移动磁盘未挂载)。
    /// </summary>
    public static async Task<(List<PhotoItem> Photos, int MissingCount)> LoadAsync()
    {
        var result = new List<PhotoItem>();
        var missing = 0;
        try
        {
            if (!File.Exists(LibraryPath)) return (result, 0);

            await using var fs = File.OpenRead(LibraryPath);
            var lib = await JsonSerializer.DeserializeAsync<LibraryFile>(fs).ConfigureAwait(false);
            if (lib?.Photos == null) return (result, 0);

            foreach (var e in lib.Photos)
            {
                if (string.IsNullOrWhiteSpace(e.Path)) continue;
                try
                {
                    var fi = new FileInfo(e.Path);
                    if (!fi.Exists) { missing++; continue; }
                    result.Add(new PhotoItem
                    {
                        Filename = fi.Name,
                        FilePath = fi.FullName,
                        FileSize = fi.Length,
                        DateAdded = e.Added,
                        DateTaken = e.Taken,
                        IsFavorite = e.Favorite,
                        Rating = e.Rating,
                        Category = e.Category ?? "",
                    });
                }
                catch { missing++; }
            }
        }
        catch { /* 文件损坏时以空库启动，避免崩溃 */ }
        return (result, missing);
    }

    public static async Task SaveAsync(IEnumerable<PhotoItem> photos)
    {
        var lib = new LibraryFile
        {
            Photos = photos.Select(p => new Entry
            {
                Path = p.FilePath,
                Favorite = p.IsFavorite,
                Rating = p.Rating,
                Added = p.DateAdded,
                Category = p.Category ?? "",
                Taken = p.DateTaken,
            }).ToList(),
        };

        // 先写临时文件再替换，避免写入中断导致 JSON 损坏
        var tmp = LibraryPath + ".tmp";
        try
        {
            await using (var fs = File.Create(tmp))
            {
                // ConfigureAwait(false)：允许调用方(如窗口关闭时)同步 Wait 而不死锁
                await JsonSerializer.SerializeAsync(fs, lib, new JsonSerializerOptions { WriteIndented = false }).ConfigureAwait(false);
            }
            File.Move(tmp, LibraryPath, overwrite: true);
        }
        catch { /* 保存失败不影响使用，下次再试 */ }
    }
}
