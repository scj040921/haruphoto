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
    private static readonly string FilePath = Path.Combine(AppPaths.DataDirectory, "settings.json");

    // ── 主题 ──
    public bool DarkMode { get; set; } = false;
    // 主题模式（SPW 借鉴三模式）：-1=跟随系统 0=浅色 1=深色；DarkMode 保留兼容
    public int ThemeMode { get; set; } = 0;
    public string AccentColor { get; set; } = "#5B6EAE";  // 柔和蓝紫（低饱和度）
    public double Saturation { get; set; } = 0.55;         // 全局饱和度系数

    // ── 更新 ──
    public bool AutoCheckUpdates { get; set; } = true;
    public string UpdateChannel { get; set; } = UpdateService.CurrentBuild.Channel;
    public bool UsePublicUpdateProxy { get; set; } = true;
    public DateTime LastUpdateCheckUtc { get; set; } = DateTime.MinValue;

    // ── 外观（SPW 风格）──
    public bool AcrylicEnabled { get; set; } = false;      // 亚克力毛玻璃
    public double AcrylicOpacity { get; set; } = 0.55;     // 亚克力透明度 0.3-0.9
    public double CardCornerRadius { get; set; } = 14;     // 卡片圆角
    public bool AnimationsEnabled { get; set; } = true;    // 动画总开关
    public double AnimationDurationMs { get; set; } = 350; // 入场动画时长
    public int UiStyle { get; set; } = 0;                  // 0=原生简约 1=通透亚克力 2=Liquid Glass
    public double GlassTintStrength { get; set; } = 0.35;  // 玻璃遮罩强度（越低越通透）
    public double GlassHighlightStrength { get; set; } = 0.60; // 玻璃高光强度
    public bool FilmstripAutoHide { get; set; } = true;    // 详情胶片条靠近底部才出现

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
    public int DisplayMode { get; set; } = 0; // 0=规整网格 1=瀑布流
    public bool PreviewPaletteEnabled { get; set; } = true; // 详情页取色背景
    public int SlideshowIntervalSeconds { get; set; } = 4;
    public bool PreviewInfoDefaultVisible { get; set; } = true;
    public int FilmstripMode { get; set; } = 0; // 0=始终显示 1=靠近底部显示 2=隐藏
    public List<AppearanceProfile> AppearanceProfiles { get; set; } = new();
    public string ActiveAppearanceProfileId { get; set; } = "";

    public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this)) ?? new AppSettings();

    // ── 持久化 ──
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                settings.UpdateChannel = UpdateService.NormalizeChannel(settings.UpdateChannel);
                return settings;
            }
        }
        catch { /* 文件损坏时用默认值 */ }
        return new AppSettings();
    }

    public void Save()
    {
        TrySave(out _);
    }

    public bool TrySave(out string error) => TrySave(FilePath, out error);

    internal bool TrySave(string destination, out string error)
    {
        error = "";
        var temporaryPath = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var dir = System.IO.Path.GetDirectoryName(destination)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, destination, overwrite: true);
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
        finally { try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { } }
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
