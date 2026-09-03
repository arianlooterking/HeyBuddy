using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace Clicky.Windows.Views;

public sealed class RegionSelectWindow : Window
{
    private readonly Canvas canvas = new(); private readonly Rectangle selection = new() { Stroke = Brushes.White, StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(70, 56, 107, 255)) }; private System.Windows.Point start; private bool selecting;
    public System.Drawing.Rectangle Selection
    {
        get; private set;
    }
    public RegionSelectWindow()
    {
        var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0));
        Topmost = true;
        ShowInTaskbar = false;
        Cursor = System.Windows.Input.Cursors.Cross;
        Content = canvas;
        var hint = new TextBlock { Text = "Drag to share a region · Escape to cancel", Foreground = Brushes.White, FontSize = 20, Margin = new(30) };
        canvas.Children.Add(hint);
        canvas.Children.Add(selection);
        SourceInitialized += (_, _) => { var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle; SetWindowPos(handle, new nint(-1), bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0); };
        MouseLeftButtonDown += (_, e) => { start = e.GetPosition(canvas); selecting = true; CaptureMouse(); };
        MouseMove += (_, e) => { if (!selecting) return; var end = e.GetPosition(canvas); Canvas.SetLeft(selection, Math.Min(start.X, end.X)); Canvas.SetTop(selection, Math.Min(start.Y, end.Y)); selection.Width = Math.Abs(end.X - start.X); selection.Height = Math.Abs(end.Y - start.Y); };
        MouseLeftButtonUp += (_, e) => { if (!selecting) return; ReleaseMouseCapture(); var end = e.GetPosition(canvas); var a = canvas.PointToScreen(start); var b = canvas.PointToScreen(end); Selection = System.Drawing.Rectangle.FromLTRB((int)Math.Min(a.X, b.X), (int)Math.Min(a.Y, b.Y), (int)Math.Max(a.X, b.X), (int)Math.Max(a.Y, b.Y)); DialogResult = Selection.Width > 5 && Selection.Height > 5; };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) DialogResult = false; };
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetWindowPos(nint h, nint after, int x, int y, int cx, int cy, uint flags);
}
