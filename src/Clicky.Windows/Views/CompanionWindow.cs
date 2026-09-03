using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Clicky.Core;
using Path = System.Windows.Shapes.Path;

namespace Clicky.Windows.Views;

public sealed class CompanionWindow : Window
{
    private readonly AppSettings settings;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly TextBlock status = new() { FontSize = 12, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
    private readonly Border bubble;
    private readonly Path pointer;
    private readonly Canvas mascot = new() { Width = 48, Height = 62, VerticalAlignment = VerticalAlignment.Top };
    private readonly List<(MenuItem Item, double Scale)> sizes = [];
    private double x, y;
    private bool docked;
    private string reply = "";
    private DateTime replyUntil;
    public event Action<double>? ScaleChanged;
    public CompanionWindow(AppSettings settings, Action open)
    {
        this.settings = settings;
        docked = settings.CompanionDocked;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        var content = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(2) };
        pointer = new Path { Data = Geometry.Parse("M 6,4 L 43,35 L 29,38 L 21,57 Z"), Fill = new SolidColorBrush(Color.FromRgb(56, 107, 255)), Stroke = Brushes.White, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
        mascot.Children.Add(pointer);
        var eye1 = new Ellipse { Width = 4, Height = 6, Fill = Brushes.White };
        Canvas.SetLeft(eye1, 16);
        Canvas.SetTop(eye1, 22);
        mascot.Children.Add(eye1);
        var eye2 = new Ellipse { Width = 4, Height = 6, Fill = Brushes.White };
        Canvas.SetLeft(eye2, 25);
        Canvas.SetTop(eye2, 25);
        mascot.Children.Add(eye2);
        bubble = new Border { Child = status, Background = new SolidColorBrush(Color.FromRgb(29, 37, 51)), Padding = new(10, 7, 10, 7), CornerRadius = new(9), MaxWidth = 145, VerticalAlignment = VerticalAlignment.Top, Margin = new(6, 7, 0, 0), Visibility = Visibility.Collapsed };
        content.Children.Add(mascot);
        content.Children.Add(bubble);
        Content = content;
        ToolTip = "Double-click to open HeyBuddy. Right-click for size and docking.";
        MouseDoubleClick += (_, _) => open();
        var menu = new ContextMenu();
        var openItem = new MenuItem { Header = "Open HeyBuddy" };
        openItem.Click += (_, _) => open();
        menu.Items.Add(openItem);
        var dock = new MenuItem { Header = "Dock / follow cursor" };
        dock.Click += (_, _) => { docked = !docked; settings.CompanionDocked = docked; settings.Save(); };
        menu.Items.Add(dock);
        menu.Items.Add(new Separator());
        foreach (var (label, scale) in new[] { ("Small (50%)", .5), ("Normal (100%)", 1.0), ("Large (200%)", 2.0) })
        {
            var item = new MenuItem { Header = label, IsCheckable = true };
            item.Click += (_, _) =>
            {
                settings.CompanionScale = scale;
                settings.Save();
                ApplySettings();
                ScaleChanged?.Invoke(scale);
            };
            sizes.Add((item, scale));
            menu.Items.Add(item);
        }
        ContextMenu = menu;
        SourceInitialized += (_, _) => { var handle = new WindowInteropHelper(this).Handle; SetWindowLongPtr(handle, -20, new nint(GetWindowLongPtr(handle, -20).ToInt64() | 0x08000000 | 0x80)); };
        timer.Tick += (_, _) => Follow();
        Loaded += (_, _) => { ApplySettings(); timer.Start(); };
        Closed += (_, _) => timer.Stop();
        ApplySettings();
    }
    public void ApplySettings()
    {
        try
        {
            pointer.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(settings.CompanionColor));
        }
        catch (FormatException) { }
        var scale = double.IsFinite(settings.CompanionScale) ? Math.Clamp(settings.CompanionScale, .5, 2) : .5;
        mascot.LayoutTransform = new ScaleTransform(scale, scale);
        foreach (var (item, value) in sizes)
            item.IsChecked = Math.Abs(scale - value) < .001;
    }
    public void SetReply(string text)
    {
        reply = text.Length > 95 ? text[..92] + "…" : text;
        replyUntil = DateTime.UtcNow.AddSeconds(12);
        SetState("");
    }
    public void SetState(string value)
    {
        if (string.IsNullOrEmpty(value) && DateTime.UtcNow < replyUntil)
            value = reply;
        status.Text = value;
        status.FlowDirection = MainWindow.DetectLanguage(value) == "fa" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        bubble.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
    }
    private void Follow()
    {
        if (!IsVisible || IsMouseOver || ContextMenu?.IsOpen == true)
            return;
        if (status.Text == reply && DateTime.UtcNow >= replyUntil)
        {
            reply = "";
            SetState("");
        }
        var cursor = System.Windows.Forms.Cursor.Position;
        var screen = System.Windows.Forms.Screen.FromPoint(cursor).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX);
        var height = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
        var targetX = docked ? screen.Right - width - 20 : Math.Clamp(cursor.X + 25, screen.Left, Math.Max(screen.Left, screen.Right - width));
        var targetY = docked ? screen.Top + 18 : Math.Clamp(cursor.Y + 22, screen.Top, Math.Max(screen.Top, screen.Bottom - height));
        var ratio = settings.ReducedMotion ? 1 : 0.28;
        if (x == 0 && y == 0)
        {
            x = targetX;
            y = targetY;
        }
        x += (targetX - x) * ratio;
        y += (targetY - y) * ratio;
        SetWindowPos(new WindowInteropHelper(this).Handle, new nint(-1), (int)x, (int)y, 0, 0, 0x0010 | 0x0001);
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint h, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint h, int index, nint value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint h, nint after, int x, int y, int cx, int cy, uint flags);
}
