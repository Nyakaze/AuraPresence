using System.Runtime.InteropServices;

namespace Aura.Presence.App;

internal static class Native
{
    public const int NimAdd = 0;
    public const int NimModify = 1;
    public const int NimDelete = 2;
    public const int NimSetVersion = 4;
    public const int NifMessage = 0x1;
    public const int NifIcon = 0x2;
    public const int NifTip = 0x4;
    public const int NifShowTip = 0x80;
    public const int NotifyIconVersion4 = 4;
    public const int NinSelect = 0x400;
    public const int NinKeySelect = 0x401;
    public const int WmContextMenu = 0x7B;
    public const int WmApp = 0x8000;
    public const int SmCxSmallIcon = 49;
    public const int AllowAnyProcess = -1;
    public const int DwmUseImmersiveDarkMode = 20;
    public const int DwmBorderColor = 34;
    public const int DwmCaptionColor = 35;
    public const int DwmTextColor = 36;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NotifyIconData
    {
        public int Size;
        public IntPtr Window;
        public int Id;
        public int Flags;
        public int CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public int State;
        public int StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public int Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public int InfoFlags;
        public Guid Item;
        public IntPtr BalloonIcon;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    public static extern bool ShellNotifyIcon(int message, ref NotifyIconData data);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
    public static extern int RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    public static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    public static extern IntPtr CreateIconFromResourceEx(byte[] bits, int size, bool icon, int version, int width, int height, int flags);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr icon);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
