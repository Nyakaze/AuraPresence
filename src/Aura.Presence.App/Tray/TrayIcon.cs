using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Aura.Presence.App.Tray;

public sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = Native.WmApp + 1;

    private readonly HwndSource _window;
    private readonly int _taskbarCreated;
    private readonly Dictionary<(bool Showing, bool Problem), IntPtr> _icons = [];
    private (bool Showing, bool Problem) _look;
    private string _tooltip = "Aura";

    public TrayIcon()
    {
        _window = new HwndSource(new HwndSourceParameters("AuraTray") { WindowStyle = 0, Width = 0, Height = 0 });
        _window.AddHook(OnMessage);
        _taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
        Add();
    }

    public event Action? Activated;

    public event Action? MenuRequested;

    public void Update(bool showing, bool problem, string tooltip)
    {
        if (_look == (showing, problem) && _tooltip == tooltip)
            return;

        _look = (showing, problem);
        _tooltip = tooltip;
        var data = Data(Native.NifIcon | Native.NifTip | Native.NifShowTip);
        Native.ShellNotifyIcon(Native.NimModify, ref data);
    }

    public void ShowMenu(ContextMenu menu)
    {
        Native.SetForegroundWindow(_window.Handle);
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    public void Dispose()
    {
        var data = Data(0);
        Native.ShellNotifyIcon(Native.NimDelete, ref data);
        _window.Dispose();

        foreach (var icon in _icons.Values)
            Native.DestroyIcon(icon);

        _icons.Clear();
    }

    private IntPtr OnMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == CallbackMessage)
        {
            switch ((int)(lParam.ToInt64() & 0xFFFF))
            {
                case Native.NinSelect or Native.NinKeySelect:
                    Activated?.Invoke();
                    break;
                case Native.WmContextMenu:
                    MenuRequested?.Invoke();
                    break;
            }

            handled = true;
        }
        else if (message == _taskbarCreated)
        {
            Add();
        }

        return IntPtr.Zero;
    }

    private void Add()
    {
        var data = Data(Native.NifMessage | Native.NifIcon | Native.NifTip | Native.NifShowTip);
        Native.ShellNotifyIcon(Native.NimAdd, ref data);
        data.Version = Native.NotifyIconVersion4;
        Native.ShellNotifyIcon(Native.NimSetVersion, ref data);
    }

    private Native.NotifyIconData Data(int flags) => new()
    {
        Size = Marshal.SizeOf<Native.NotifyIconData>(),
        Window = _window.Handle,
        Id = 1,
        Flags = flags,
        CallbackMessage = CallbackMessage,
        Icon = IconFor(_look),
        Tip = _tooltip,
        Info = "",
        InfoTitle = ""
    };

    private IntPtr IconFor((bool Showing, bool Problem) look)
    {
        if (_icons.TryGetValue(look, out var icon))
            return icon;

        var size = Native.GetSystemMetrics(Native.SmCxSmallIcon);
        var png = AuraMark.RenderPng(size, look.Showing, look.Problem);
        icon = Native.CreateIconFromResourceEx(png, png.Length, true, 0x00030000, size, size, 0);
        _icons[look] = icon;

        return icon;
    }
}

public static class AuraMark
{
    private static readonly Brush Live = Frozen(0x9D, 0x7C, 0xF4);
    private static readonly Brush Idle = Frozen(0x8E, 0x8E, 0x96);
    private static readonly Brush Problem = Frozen(0xF2, 0xB4, 0x5A);
    private static readonly Brush Outline = Frozen(0x0C, 0x0C, 0x0E);

    public static byte[] RenderPng(int size, bool live, bool problem)
    {
        var visual = new DrawingVisual();

        using (var context = visual.RenderOpen())
        {
            var center = new Point(size / 2.0, size / 2.0);
            var outer = size / 2.0 - size / 32.0;
            var ring = new GeometryGroup { FillRule = FillRule.EvenOdd };
            ring.Children.Add(new EllipseGeometry(center, outer, outer));
            ring.Children.Add(new EllipseGeometry(center, outer * 0.45, outer * 0.45));
            context.DrawGeometry(live ? Live : Idle, null, ring);

            if (problem)
            {
                var radius = size * 0.21;
                var dot = new Point(size - radius - size / 32.0, size - radius - size / 32.0);
                context.DrawEllipse(Problem, new Pen(Outline, size / 16.0), dot, radius, radius);
            }
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);

        return stream.ToArray();
    }

    private static Brush Frozen(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();

        return brush;
    }
}
