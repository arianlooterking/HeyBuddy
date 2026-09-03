using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Clicky.Windows.Views;

/// <summary>A click-through action marker. It observes approved actions and has no input capability.</summary>
public sealed class ActionCursorWindow : Window
{
    private readonly Ellipse outer;
    private readonly Ellipse inner;
    private readonly DispatcherTimer hideTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };

    public ActionCursorWindow(string color)
    {
        Width = 48;
        Height = 48;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        IsHitTestVisible = false;
        var accent = Parse(color);
        outer = new Ellipse { Width = 42, Height = 42, Stroke = accent, StrokeThickness = 4, Fill = new SolidColorBrush(Color.FromArgb(28, ((SolidColorBrush)accent).Color.R, ((SolidColorBrush)accent).Color.G, ((SolidColorBrush)accent).Color.B)) };
        inner = new Ellipse { Width = 10, Height = 10, Fill = accent, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Content = new Grid { Children = { outer, inner } };
        Opacity = 0;
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLongPtr(handle, -20, new nint(GetWindowLongPtr(handle, -20).ToInt64() | 0x08000000 | 0x20 | 0x80));
        };
        hideTimer.Tick += (_, _) => { hideTimer.Stop(); Opacity = 0; };
    }

    public void ShowAt(int x, int y, string color)
    {
        var accent = Parse(color);
        outer.Stroke = accent;
        var selected = ((SolidColorBrush)accent).Color;
        outer.Fill = new SolidColorBrush(Color.FromArgb(28, selected.R, selected.G, selected.B));
        inner.Fill = accent;
        if (!IsVisible)
            Show();
        SetWindowPos(new WindowInteropHelper(this).Handle, new nint(-1), x - 24, y - 24, 48, 48, 0x0010);
        Opacity = 1;
        hideTimer.Stop();
        hideTimer.Start();
    }

    private static Brush Parse(string value)
    {
        try
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        }
        catch (Exception error) when (error is FormatException or NotSupportedException or InvalidOperationException)
        {
            return new SolidColorBrush(Color.FromRgb(56, 107, 255));
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint h, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint h, int index, nint value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint h, nint after, int x, int y, int cx, int cy, uint flags);
}
