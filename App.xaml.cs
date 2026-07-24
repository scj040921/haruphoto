using Microsoft.UI.Xaml;
using System;

namespace PhotoAlbum;

public partial class App : Application
{
    private Window? _window;
    internal static AppSettings Settings { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
        Settings = AppSettings.Load();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();

        // 首次运行自动创建桌面快捷方式
        if (Settings.FirstRun)
        {
            ShortcutService.EnsureDesktopShortcut();
            Settings.FirstRun = false;
            Settings.Save();
        }
    }
}
