using System;
using System.Collections.Generic;

namespace PhotoAlbum;

/// <summary>只保存展示偏好，套用方案不会改变监控文件夹或更新设置。</summary>
public sealed class AppearanceProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "我的方案";
    public int ThemeMode { get; set; }
    public string AccentColor { get; set; } = "#5B6EAE";
    public bool AcrylicEnabled { get; set; }
    public double AcrylicOpacity { get; set; } = .55;
    public int TopBarStyle { get; set; }
    public double CardCornerRadius { get; set; } = 14;
    public int DisplayMode { get; set; }
    public double CardMinWidth { get; set; } = 220;
    public bool AnimationsEnabled { get; set; } = true;
    public double AnimationDurationMs { get; set; } = 350;
    public int BackgroundMode { get; set; }
    public string BackgroundColor { get; set; } = "#2A2A32";
    public string BackgroundImagePath { get; set; } = "";
    public bool PreviewPaletteEnabled { get; set; } = true;
    public bool PreviewInfoDefaultVisible { get; set; } = true;
    public int FilmstripMode { get; set; }
    public int SlideshowIntervalSeconds { get; set; } = 4;

    public static AppearanceProfile Capture(AppSettings s, string name) => new()
    {
        Name = name, ThemeMode = s.ThemeMode, AccentColor = s.AccentColor,
        AcrylicEnabled = s.AcrylicEnabled, AcrylicOpacity = s.AcrylicOpacity,
        TopBarStyle = s.TopBarStyle, CardCornerRadius = s.CardCornerRadius,
        DisplayMode = s.DisplayMode, CardMinWidth = s.CardMinWidth,
        AnimationsEnabled = s.AnimationsEnabled, AnimationDurationMs = s.AnimationDurationMs,
        BackgroundMode = s.BackgroundMode, BackgroundColor = s.BackgroundColor,
        BackgroundImagePath = s.BackgroundImagePath, PreviewPaletteEnabled = s.PreviewPaletteEnabled,
        PreviewInfoDefaultVisible = s.PreviewInfoDefaultVisible, FilmstripMode = s.FilmstripMode,
        SlideshowIntervalSeconds = s.SlideshowIntervalSeconds,
    };

    public void ApplyTo(AppSettings s)
    {
        s.ThemeMode = Math.Clamp(ThemeMode, -1, 1);
        s.DarkMode = s.ThemeMode == 1;
        s.AccentColor = AccentColor;
        s.AcrylicEnabled = AcrylicEnabled;
        s.AcrylicOpacity = Math.Clamp(AcrylicOpacity, 0, 1);
        s.TopBarStyle = Math.Clamp(TopBarStyle, 0, 1);
        s.CardCornerRadius = Math.Clamp(CardCornerRadius, 0, 24);
        s.DisplayMode = Math.Clamp(DisplayMode, 0, 1);
        s.CardMinWidth = Math.Clamp(CardMinWidth, 160, 360);
        s.AnimationsEnabled = AnimationsEnabled;
        s.AnimationDurationMs = Math.Clamp(AnimationDurationMs, 150, 600);
        s.BackgroundMode = Math.Clamp(BackgroundMode, 0, 2);
        s.BackgroundColor = BackgroundColor;
        s.BackgroundImagePath = BackgroundImagePath;
        s.PreviewPaletteEnabled = PreviewPaletteEnabled;
        s.PreviewInfoDefaultVisible = PreviewInfoDefaultVisible;
        s.FilmstripMode = Math.Clamp(FilmstripMode, 0, 2);
        s.SlideshowIntervalSeconds = Math.Clamp(SlideshowIntervalSeconds, 2, 15);
    }

    public static List<AppearanceProfile> BuiltIns() => new()
    {
        new() { Id = "builtin-dark", Name = "深色看图", ThemeMode = 1, DisplayMode = 1, CardMinWidth = 240, FilmstripMode = 1 },
        new() { Id = "builtin-light", Name = "浅色整理", ThemeMode = 0, DisplayMode = 0, CardMinWidth = 200 },
        new() { Id = "builtin-immersive", Name = "沉浸展示", ThemeMode = 1, DisplayMode = 1, CardMinWidth = 300,
            AcrylicEnabled = true, TopBarStyle = 1, PreviewInfoDefaultVisible = false, FilmstripMode = 1, SlideshowIntervalSeconds = 6 },
    };
}
