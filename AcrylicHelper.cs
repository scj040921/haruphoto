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

    private const int WCA_ACCENT_POLICY = 19;

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
