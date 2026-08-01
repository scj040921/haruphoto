using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace PhotoAlbum;

/// <summary>
/// 重复照片检测。先按文件大小分组预筛，再对同大小的文件计算 SHA256，
/// 哈希相同的即为重复照片。大文件读取在后台线程执行，不阻塞 UI。
/// </summary>
public static class DuplicateScanner
{
    /// <summary>查找重复照片，返回重复组列表（每组 ≥2 张）。progress 报告已检查的文件数。</summary>
    public static Task<List<List<PhotoItem>>> FindDuplicatesAsync(IEnumerable<PhotoItem> photos, IProgress<int>? progress = null)
        => Task.Run(() =>
        {
            var all = photos.ToList();

            // 1. 按文件大小分组，大小唯一的文件不可能是重复的
            var bySize = all
                .Where(p => File.Exists(p.FilePath))
                .GroupBy(p => p.FileSize)
                .Where(g => g.Count() > 1)
                .ToList();

            var checkedCount = 0;
            var totalToCheck = bySize.Sum(g => g.Count());
            var hashMap = new Dictionary<string, List<PhotoItem>>(StringComparer.OrdinalIgnoreCase);

            foreach (var group in bySize)
            {
                foreach (var photo in group)
                {
                    try
                    {
                        var hash = ComputeSha256(photo.FilePath);
                        if (!hashMap.TryGetValue(hash, out var list))
                            hashMap[hash] = list = new List<PhotoItem>();
                        list.Add(photo);
                    }
                    catch { /* 文件读取失败（占用/损坏）跳过 */ }
                    checkedCount++;
                    progress?.Report(checkedCount);
                }
            }

            return hashMap.Values.Where(l => l.Count > 1).ToList();
        });

    private static string ComputeSha256(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(fs));
    }
}
