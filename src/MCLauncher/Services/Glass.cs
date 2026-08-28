using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MCLauncher.Services;

/// <summary>
/// Включает «морозное стекло» (DWM blur-behind) для прозрачных окон лаунчера,
/// чтобы фон рабочего стола размывался за стеклянными панелями.
/// </summary>
public static class Glass
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_BLURBEHIND
    {
        public uint dwFlags;
        public int fEnable;
        public IntPtr hRgnBlur;
        public int fTransitionOnMaximized;
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmEnableBlurBehindWindow(IntPtr hwnd, ref DWM_BLURBEHIND pBlurBehind);

    private const uint DWM_BB_ENABLE = 0x1;

    public static void Enable(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            var bb = new DWM_BLURBEHIND
            {
                dwFlags = DWM_BB_ENABLE,
                fEnable = 1,
                hRgnBlur = IntPtr.Zero,
                fTransitionOnMaximized = 0
            };
            DwmEnableBlurBehindWindow(hwnd, ref bb);
        }
        catch
        {
            // DWM недоступен (старая ОС/без композиции) — остаётся просто прозрачное окно.
        }
    }
}
