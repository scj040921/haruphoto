using System;
using System.Runtime.InteropServices;
using Windows.UI;

namespace PhotoAlbum;

/// <summary>
/// Win32 亚克力毛玻璃。使用 SetWindowCompositionAttribute (ACCENT_ENABLE_ACRYLICBLURBEHIND)，
/// 比 MicaBackdrop/DesktopAcrylicBackdrop 在非打包模式下稳定得多（不依赖 SystemBackdrop 管线）。
/// 仅 Windows 10 1803+ 可用；不支持时静默失败，应用保持原有外观。
/// </summary>
public static class AcrylicHelper
{
    private enum AccentState
    {
        Disabled = 0,
        EnableGradient = 1,
        EnableTransparentGradient = 2,
        EnableBlurBehind = 3,
        EnableAcrylicBlurBehind = 4,
        EnableHostBackdrop = 5,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int WCA_ACCENT_POLICY = 19;
    // Win11 22H2+ 官方：DWMWA_SYSTEMBACKDROP_TYPE = 38
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_TRANSIENTWINDOW = 3;   // Acrylic（瞬态亚克力：模糊窗口下方）
    private const int DWMSBT_MAINWINDOW = 2;        // Mica
    private const int DWMSBT_NONE = 1;

    /// <summary>
    /// Win11 22H2+ 官方亚克力：DwmSetWindowAttribute(DWMWA_SYSTEMBACKDROP_TYPE, DWMSBT_TRANSIENTWINDOW)。
    /// 比 SetWindowCompositionAttribute 可靠（AccentPolicy 在 Win11 24H2 已失效）。
    /// 返回 false = 系统不支持（Win10/旧版）→ 调用方回退其他方案。
    /// </summary>
    public static bool EnableSystemBackdrop(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return false;
            int backdrop = DWMSBT_TRANSIENTWINDOW;   // Acrylic 亚克力
            return DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int)) == 0;
        }
        catch { return false; }
    }

    /// <summary>关闭 DWM 系统背景（恢复默认）</summary>
    public static bool DisableSystemBackdrop(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return false;
            int backdrop = DWMSBT_NONE;
            return DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int)) == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// 启用亚克力。tint 为色调，opacity 0-1 为磨砂不透明度。
    /// 失败（旧系统/权限）时静默返回 false，不影响应用。
    /// </summary>
    public static bool Enable(IntPtr hwnd, Color tint, double opacity)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return false;
            var a = Math.Clamp((int)(opacity * 255), 20, 255);
            var accent = new AccentPolicy
            {
                AccentState = (int)AccentState.EnableAcrylicBlurBehind,
                AccentFlags = 2, // DrawAllBorders
                // GradientColor 布局：AABBGGRR（Win32 小端 ABGR）
                GradientColor = (uint)((a << 24) | (tint.B << 16) | (tint.G << 8) | tint.R),
            };
            var data = new WindowCompositionAttributeData
            {
                Attribute = WCA_ACCENT_POLICY,
                Data = Marshal.AllocHGlobal(Marshal.SizeOf<AccentPolicy>()),
                SizeOfData = Marshal.SizeOf<AccentPolicy>(),
            };
            try
            {
                Marshal.StructureToPtr(accent, data.Data, false);
                return SetWindowCompositionAttribute(hwnd, ref data) != 0;
            }
            finally
            {
                Marshal.FreeHGlobal(data.Data);
            }
        }
        catch { return false; }
    }

    /// <summary>关闭亚克力，恢复原有窗口背景。</summary>
    public static bool Disable(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return false;
            var accent = new AccentPolicy { AccentState = (int)AccentState.Disabled };
            var data = new WindowCompositionAttributeData
            {
                Attribute = WCA_ACCENT_POLICY,
                Data = Marshal.AllocHGlobal(Marshal.SizeOf<AccentPolicy>()),
                SizeOfData = Marshal.SizeOf<AccentPolicy>(),
            };
            try
            {
                Marshal.StructureToPtr(accent, data.Data, false);
                return SetWindowCompositionAttribute(hwnd, ref data) != 0;
            }
            finally
            {
                Marshal.FreeHGlobal(data.Data);
            }
        }
        catch { return false; }
    }
}
