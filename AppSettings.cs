using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoAlbum;

/// <summary>
/// 应用设置 —— 主题、自动扫描、监控文件夹等。持久化到 %LocalAppData%\haruphoto\settings.json
/// </summary>
public sealed class AppSettings
{
    private static readonly string FilePath =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                               "haruphoto", "settings.json");

    // ── 主题 ──
    public bool DarkMode { get; set; } = false;
    public string AccentColor { get; set; } = "#5B6EAE";  // 柔和蓝紫（低饱和度）
    public double Saturation { get; set; } = 0.55;         // 全局饱和度系数

    // ── 外观（SPW 风格）──
    public bool AcrylicEnabled { get; set; } = false;      // 亚克力毛玻璃
    public double AcrylicOpacity { get; set; } = 0.55;     // 亚克力透明度 0.3-0.9
    public double CardCornerRadius { get; set; } = 14;     // 卡片圆角
    public bool AnimationsEnabled { get; set; } = true;    // 动画总开关
    public double AnimationDurationMs { get; set; } = 350; // 入场动画时长

    // ── 背景 ──
    public int BackgroundMode { get; set; } = 0;           // 0=默认 1=纯色 2=图片
    public string BackgroundColor { get; set; } = "#2A2A32";
    public string BackgroundImagePath { get; set; } = "";
    public int TopBarStyle { get; set; } = 0;              // 顶栏材质：0=侧边栏同款 1=液态玻璃（扭曲折射）

    // ── 自动扫描 ──
    public bool AutoScan { get; set; } = true;
    public int AutoScanIntervalMinutes { get; set; } = 5;
    public List<string> WatchedFolders { get; set; } = new();

    // ── 首次运行 ──
    public bool FirstRun { get; set; } = true;

    // ── 布局 ──
    public double CardMinWidth { get; set; } = 220;
    public int SortMode { get; set; } = 0;   // 0=最近添加, 1=文件名, 2=评分

    // ── 持久化 ──
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch { /* 文件损坏时用默认值 */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch { /* 保存失败不崩溃 */ }
    }

    /// <summary>将 AccentColor hex 转为 Windows.UI.Color</summary>
    public Windows.UI.Color GetAccentColor()
    {
        try
        {
            var hex = AccentColor.TrimStart('#');
            return new Windows.UI.Color
            {
                A = 255,
                R = Convert.ToByte(hex[..2], 16),
                G = Convert.ToByte(hex[2..4], 16),
                B = Convert.ToByte(hex[4..6], 16),
            };
        }
        catch { return Windows.UI.Color.FromArgb(255, 91, 110, 174); }
    }
}
