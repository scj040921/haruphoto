using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace PhotoAlbum;

public sealed class LibraryReadException : IOException
{
    public LibraryReadException(string message, Exception innerException) : base(message, innerException) { }
}

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
        [JsonPropertyName("latitude")] public double? Latitude { get; set; }
        [JsonPropertyName("longitude")] public double? Longitude { get; set; }
        [JsonPropertyName("placeId")] public string PlaceId { get; set; } = "";
        [JsonPropertyName("placeLabel")] public string PlaceLabel { get; set; } = "";
        [JsonPropertyName("placeNote")] public string PlaceNote { get; set; } = "";
    }

    private sealed class LibraryFile
    {
        [JsonPropertyName("version")] public int Version { get; set; } = 1;
        [JsonPropertyName("photos")] public List<Entry>? Photos { get; set; }
    }

    private static readonly string DataDir = AppPaths.DataDirectory;
    private static readonly System.Threading.SemaphoreSlim SaveGate = new(1);
    private static readonly List<Entry> MissingEntries = new();
    private static bool _writeBlocked;

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
        var offline = new List<Entry>();
        await SaveGate.WaitAsync().ConfigureAwait(false);
        _writeBlocked = true;
        try
        {
            await using var fs = File.OpenRead(LibraryPath);
            var lib = await JsonSerializer.DeserializeAsync<LibraryFile>(fs).ConfigureAwait(false);
            if (lib?.Photos == null || lib.Version < 1 || lib.Version > 1)
                throw new InvalidDataException("图库文件格式或版本不受支持。");

            foreach (var e in lib.Photos)
            {
                if (string.IsNullOrWhiteSpace(e.Path)) continue;
                try
                {
                    var fi = new FileInfo(e.Path);
                    if (!fi.Exists) { offline.Add(e); missing++; continue; }
                    result.Add(new PhotoItem
                    {
                        Filename = fi.Name,
                        FilePath = fi.FullName,
                        FileSize = fi.Length,
                        DateAdded = e.Added,
                        // 读取时直接以源文件修改时间覆盖旧版持久化的 EXIF 日期。
                        DateTaken = fi.LastWriteTime,
                        IsFavorite = e.Favorite,
                        Rating = e.Rating,
                        Category = e.Category ?? "",
                        MapLatitude = MapGeometry.Valid(e.Latitude, e.Longitude) ? e.Latitude : null,
                        MapLongitude = MapGeometry.Valid(e.Latitude, e.Longitude) ? e.Longitude : null,
                        MapPlaceId = e.PlaceId ?? "",
                        MapLabel = e.PlaceLabel ?? "",
                        MapNote = e.PlaceNote ?? "",
                    });
                }
                catch { offline.Add(e); missing++; }
            }
            lock (MissingEntries) { MissingEntries.Clear(); MissingEntries.AddRange(offline); }
            _writeBlocked = false;
            return (result, missing);
        }
        catch (FileNotFoundException)
        {
            lock (MissingEntries) MissingEntries.Clear();
            _writeBlocked = false;
            return (result, 0);
        }
        catch (Exception ex)
        {
            throw new LibraryReadException("图库文件损坏或暂时无法访问，原文件已保留。", ex);
        }
        finally { SaveGate.Release(); }
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
                Latitude = MapGeometry.Valid(p.MapLatitude, p.MapLongitude) ? p.MapLatitude : null,
                Longitude = MapGeometry.Valid(p.MapLatitude, p.MapLongitude) ? p.MapLongitude : null,
                PlaceId = p.MapPlaceId,
                PlaceLabel = p.MapLabel,
                PlaceNote = p.MapNote,
            }).ToList(),
        };
        // 先写临时文件再替换，避免写入中断导致 JSON 损坏
        var tmp = LibraryPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await SaveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_writeBlocked) throw new InvalidOperationException("图库读取失败，已暂停保存以保护原文件。请先重新加载图库。");
            var entries = lib.Photos!;
            var paths = new HashSet<string>(entries.Select(p => p.Path), StringComparer.OrdinalIgnoreCase);
            lock (MissingEntries) entries.AddRange(MissingEntries.Where(e => !paths.Contains(e.Path)));
            await using (var fs = File.Create(tmp))
            {
                // ConfigureAwait(false)：允许调用方(如窗口关闭时)同步 Wait 而不死锁
                await JsonSerializer.SerializeAsync(fs, lib, new JsonSerializerOptions { WriteIndented = false }).ConfigureAwait(false);
            }
            // Windows 的索引器或防护软件可能短暂占用目标文件；有限重试后仍将错误反馈给调用方。
            for (var attempt = 0; ; attempt++)
            {
                try { File.Move(tmp, LibraryPath, overwrite: true); break; }
                catch (Exception ex) when (attempt < 4 && (ex is IOException || ex is UnauthorizedAccessException))
                { await Task.Delay(50 << attempt).ConfigureAwait(false); }
            }
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            SaveGate.Release();
        }
    }

    public static void ForgetMissingEntries() { lock (MissingEntries) MissingEntries.Clear(); }
}
