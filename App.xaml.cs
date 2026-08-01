using Microsoft.UI.Xaml;
using System;
using System.Runtime.InteropServices;

namespace PhotoAlbum;

public partial class App : Application
{
    private Window? _window;
    internal static AppSettings Settings { get; private set; } = null!;
    private static Mutex? _instanceMutex;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    private const int SW_RESTORE = 9;

    public App()
    {
        InitializeComponent();
        Settings = AppSettings.Load();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // 单实例（SPW 借鉴）：重复启动 → 激活已有窗口而不是开新实例。
        // 命名互斥体检测 + 找到同进程已有主窗口置前。
        _instanceMutex = new Mutex(true, "haruphoto_single_instance", out var createdNew);
        if (!createdNew)
        {
            ActivateExistingWindow();
            // 不启动新窗口：直接退出
            Environment.Exit(0);
            return;
        }

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

    /// <summary>找到已在运行的 haruphoto 主窗口并置前（SPW 启动行为）</summary>
    private static void ActivateExistingWindow()
    {
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("PhotoAlbum"))
            {
                var hwnd = p.MainWindowHandle;
                if (hwnd != IntPtr.Zero)
                {
                    ShowWindow(hwnd, SW_RESTORE);   // 最小化时还原
                    SetForegroundWindow(hwnd);
                    return;
                }
            }
        }
        catch { /* 找不到已有窗口就静默（用户手动再开） */ }
    }
}
