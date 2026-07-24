using System;
using System.IO;
using System.Runtime.InteropServices;

namespace PhotoAlbum;

/// <summary>
/// 桌面快捷方式管理。首次运行时自动在桌面创建 haruphoto 快捷方式。
/// </summary>
public static class ShortcutService
{
    public static void EnsureDesktopShortcut()
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var shortcutPath = Path.Combine(desktop, "haruphoto.lnk");
            if (File.Exists(shortcutPath)) return;

            var exePath = Path.Combine(AppContext.BaseDirectory, "PhotoAlbum.exe");
            if (!File.Exists(exePath)) return;

            CreateShortcut(shortcutPath, exePath, "haruphoto - 照片管理", AppContext.BaseDirectory);
        }
        catch { /* 非关键功能 */ }
    }

    private static void CreateShortcut(string shortcutPath, string targetPath, string description, string workingDir)
    {
        // 使用 WshShell COM 接口创建 .lnk
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return;

        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = targetPath;
        shortcut.Description = description;
        shortcut.WorkingDirectory = workingDir;
        shortcut.IconLocation = targetPath + ",0";
        shortcut.Save();

        Marshal.ReleaseComObject(shortcut);
        Marshal.ReleaseComObject(shell);
    }
}
