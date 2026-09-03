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
    private readonly Ellipse eye1 = new() { Width = 4, Height = 6, Fill = Brushes.White };
    private readonly Ellipse eye2 = new() { Width = 4, Height = 6, Fill = Brushes.White };
    private readonly Canvas listeningIndicator = new() { Name = "ListeningIndicator", Width = 36, Height = 36, Visibility = Visibility.Collapsed };
    private readonly Ellipse listeningRing = new() { Width = 34, Height = 34, StrokeThickness = 2, Opacity = .35 };
    private readonly Ellipse listeningDisc = new() { Width = 30, Height = 30 };
    private readonly List<Rectangle> listeningBars = [];
    private readonly Canvas mascot = new() { Width = 48, Height = 62, VerticalAlignment = VerticalAlignment.Top };
    private readonly List<(MenuItem Item, double Scale)> sizes = [];
    private double x, y;
    private bool docked;
    private string reply = "";
    private DateTime replyUntil;
    private bool listening;
    private double listeningLevel;
    private double listeningTarget;
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
        Canvas.SetLeft(eye1, 16);
        Canvas.SetTop(eye1, 22);
        mascot.Children.Add(eye1);
        Canvas.SetLeft(eye2, 25);
        Canvas.SetTop(eye2, 25);
        mascot.Children.Add(eye2);
        Canvas.SetLeft(listeningRing, 1);
        Canvas.SetTop(listeningRing, 1);
        listeningRing.RenderTransformOrigin = new(.5, .5);
        listeningRing.RenderTransform = new ScaleTransform(1, 1);
        listeningIndicator.Children.Add(listeningRing);
        Canvas.SetLeft(listeningDisc, 3);
        Canvas.SetTop(listeningDisc, 3);
        listeningIndicator.Children.Add(listeningDisc);
        foreach (var left in new[] { 11d, 16.5, 22d })
        {
            var bar = new Rectangle { Width = 3.5, Height = 5, RadiusX = 1.75, RadiusY = 1.75, Fill = Brushes.White };
            Canvas.SetLeft(bar, left);
            Canvas.SetTop(bar, 15.5);
            listeningBars.Add(bar);
            listeningIndicator.Children.Add(bar);
        }
        mascot.Children.Add(listeningIndicator);
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
            var color = (Color)ColorConverter.ConvertFromString(settings.CompanionColor);
            pointer.Fill = new SolidColorBrush(color);
            listeningDisc.Fill = new SolidColorBrush(color);
            listeningRing.Stroke = new SolidColorBrush(color);
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
        if (listening)
        {
            status.Text = "";
            bubble.Visibility = Visibility.Collapsed;
            return;
        }
        if (string.IsNullOrEmpty(value) && DateTime.UtcNow < replyUntil)
            value = reply;
        status.Text = value;
        status.FlowDirection = MainWindow.DetectLanguage(value) == "fa" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        bubble.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
    }
    public void SetListening(bool active)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => SetListening(active));
            return;
        }
        listening = active;
        pointer.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        eye1.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        eye2.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        listeningIndicator.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        mascot.Width = active ? 36 : 48;
        mascot.Height = active ? 36 : 62;
        if (active)
        {
            status.Text = "";
            bubble.Visibility = Visibility.Collapsed;
            listeningLevel = 0;
            listeningTarget = 0;
            UpdateListeningVisual();
        }
        else
        {
            listeningLevel = 0;
            listeningTarget = 0;
            SetState("");
        }
    }
    public void SetAudioLevel(float level)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => SetAudioLevel(level));
            return;
        }
        if (!listening)
            return;
        var decibels = 20 * Math.Log10(Math.Max(level, .000001));
        listeningTarget = Math.Clamp((decibels + 58) / 46, 0, 1);
        listeningLevel = Math.Max(listeningLevel, listeningTarget);
        UpdateListeningVisual();
    }
    private void UpdateListeningVisual()
    {
        var accents = new[] { .72, 1.0, .82 };
        for (var index = 0; index < listeningBars.Count; index++)
        {
            var height = 4 + 17 * Math.Clamp(listeningLevel * accents[index], 0, 1);
            listeningBars[index].Height = height;
            Canvas.SetTop(listeningBars[index], 18 - height / 2);
        }
        listeningRing.Opacity = .3 + listeningLevel * .6;
        var scale = 1 + listeningLevel * .12;
        listeningRing.RenderTransform = new ScaleTransform(scale, scale);
    }
    private void Follow()
    {
        if (listening)
        {
            listeningLevel += (listeningTarget - listeningLevel) * .32;
            listeningTarget *= .86;
            UpdateListeningVisual();
        }
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
