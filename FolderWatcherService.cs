using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.IO;

namespace PhotoAlbum;

/// <summary>
/// 文件夹监控 —— 定时扫描已导入文件夹中新增的图片文件。
/// 在 unpackaged WinUI3 中，FileSystemWatcher + UI 更新可能崩溃，因此用轮询方式。
/// </summary>
public sealed class FolderWatcherService
{
    private readonly DispatcherTimer _timer;
    private readonly HashSet<string> _knownExtensions;

    public event Action<List<string>>? NewFilesFound;

    public FolderWatcherService()
    {
        _timer = new DispatcherTimer();
        _timer.Tick += OnTick;

        _knownExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".tif", ".tiff" };
    }

    public void Start(int intervalMinutes, List<string> watchedFolders)
    {
        Stop();
        if (watchedFolders.Count == 0) return;

        _timer.Interval = TimeSpan.FromMinutes(Math.Max(1, intervalMinutes));
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
    }

    private void OnTick(object? sender, object e)
    {
        var settings = AppSettings.Load();
        if (!settings.AutoScan || settings.WatchedFolders.Count == 0) return;

        try
        {
            var found = new List<string>();

            foreach (var folder in settings.WatchedFolders)
            {
                if (!Directory.Exists(folder)) continue;

                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.System,
                };

                foreach (var file in Directory.EnumerateFiles(folder, "*", options))
                {
                    if (!_knownExtensions.Contains(Path.GetExtension(file))) continue;
                    found.Add(file);
                }
            }

            if (found.Count > 0)
                NewFilesFound?.Invoke(found);
        }
        catch { /* 扫描失败不影响使用 */ }
    }
}
