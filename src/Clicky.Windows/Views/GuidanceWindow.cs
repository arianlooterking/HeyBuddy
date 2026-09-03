using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Clicky.Core;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Orientation = System.Windows.Controls.Orientation;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace Clicky.Windows.Views;

public sealed record GuidanceStep(int Number, int Total, string Narration, bool Completed);

public sealed class GuidanceWindow : Window
{
    private readonly ScreenCapture capture;
    private readonly IReadOnlyList<GuidanceCommand> commands;
    private readonly Canvas canvas = new();
    private readonly Window controls;
    private readonly TextBlock label;
    private readonly SolidColorBrush accent;
    private readonly int[] steps;
    private int index;
    private bool paused;
    public event Action<GuidanceStep>? StepAdvanced;

    public GuidanceWindow(ScreenCapture capture, IReadOnlyList<GuidanceCommand> commands) : this(capture, commands, "#386BFF")
    {
    }

    public GuidanceWindow(ScreenCapture capture, IReadOnlyList<GuidanceCommand> commands, string color)
    {
        this.capture = capture;
        this.commands = commands;
        accent = ParseColor(color);
        steps = commands.Select(c => c.Step).Distinct().Order().ToArray();
        Width = capture.Width;
        Height = capture.Height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Content = canvas;
        SourceInitialized += (_, _) => { var handle = new WindowInteropHelper(this).Handle; SetWindowLongPtr(handle, -20, new nint(GetWindowLongPtr(handle, -20).ToInt64() | 0x08000000 | 0x20 | 0x80)); SetWindowPos(handle, new nint(-1), capture.Left, capture.Top, capture.Width, capture.Height, 0x0010); };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(10) };
        label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new(6, 0, 15, 0), FontWeight = FontWeights.SemiBold };
        row.Children.Add(label);
        var pause = new Button { Content = "Pause", Margin = new(0, 0, 7, 0) };
        pause.Click += (_, _) => { paused = !paused; pause.Content = paused ? "Resume" : "Pause"; canvas.Visibility = paused ? Visibility.Hidden : Visibility.Visible; };
        row.Children.Add(pause);
        var next = new Button { Content = "Next", Margin = new(0, 0, 7, 0) };
        next.Click += (_, _) => Next();
        row.Children.Add(next);
        var close = new Button { Content = "Clear" };
        close.Click += (_, _) => Close();
        row.Children.Add(close);
        controls = new Window { Title = "HeyBuddy walkthrough", Content = row, SizeToContent = SizeToContent.WidthAndHeight, WindowStyle = WindowStyle.ToolWindow, ResizeMode = ResizeMode.NoResize, Topmost = true, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        Loaded += (_, _) => { Draw(); controls.Show(); };
        SizeChanged += (_, _) => Draw();
        Closed += (_, _) => controls.Close();
    }
    public void ObserveClick(System.Drawing.Point point)
    {
        if (paused || index >= steps.Length)
            return;
        if (commands.Where(c => c.Step == steps[index]).Any(c => IsTargetHit(capture, c, point)))
            Next();
    }
    internal static bool IsTargetHit(ScreenCapture capture, GuidanceCommand command, System.Drawing.Point point, int tolerance = 42)
    {
        var x = command.Kind switch
        {
            "arrow" => command.X2,
            "rectangle" => (command.X + command.X2) / 2,
            _ => command.X
        };
        var y = command.Kind switch
        {
            "arrow" => command.Y2,
            "rectangle" => (command.Y + command.Y2) / 2,
            _ => command.Y
        };
        return Math.Abs(capture.Left + x * capture.Width - point.X) <= tolerance &&
            Math.Abs(capture.Top + y * capture.Height - point.Y) <= tolerance;
    }
    private void Next()
    {
        if (index + 1 >= steps.Length)
        {
            StepAdvanced?.Invoke(new(index + 1, steps.Length, "Walkthrough complete.", true));
            Close();
            return;
        }
        index++;
        Draw();
        var narration = commands.FirstOrDefault(c => c.Step == steps[index] && !string.IsNullOrWhiteSpace(c.Label))?.Label ?? $"Continue with step {index + 1}.";
        StepAdvanced?.Invoke(new(index + 1, steps.Length, narration, false));
    }
    private void Draw()
    {
        if (steps.Length == 0)
            return;
        canvas.Children.Clear();
        var instruction = commands.FirstOrDefault(c => c.Step == steps[index] && !string.IsNullOrWhiteSpace(c.Label))?.Label;
        label.Text = $"Step {index + 1} of {steps.Length}" + (string.IsNullOrWhiteSpace(instruction) ? "" : " · " + instruction);
        foreach (var command in commands.Where(c => c.Step == steps[index]))
        {
            var x = command.X * ActualWidth;
            var y = command.Y * ActualHeight;
            var x2 = command.X2 * ActualWidth;
            var y2 = command.Y2 * ActualHeight;
            Shape shape = command.Kind switch
            {
                "arrow" => new Line { X1 = x, Y1 = y, X2 = x2, Y2 = y2 },
                "rectangle" => new Rectangle { Width = Math.Max(30, Math.Abs(x2 - x)), Height = Math.Max(30, Math.Abs(y2 - y)), RadiusX = 8, RadiusY = 8 },
                _ => new Ellipse { Width = command.Kind == "point" ? 28 : 70, Height = command.Kind == "point" ? 28 : 70 }
            };
            shape.Stroke = accent;
            shape.StrokeThickness = 4;
            shape.Fill = new SolidColorBrush(Color.FromArgb(28, accent.Color.R, accent.Color.G, accent.Color.B));
            if (command.Kind != "arrow")
            {
                Canvas.SetLeft(shape, command.Kind == "rectangle" ? Math.Min(x, x2) : x - shape.Width / 2);
                Canvas.SetTop(shape, command.Kind == "rectangle" ? Math.Min(y, y2) : y - shape.Height / 2);
            }
            canvas.Children.Add(shape);
            if (command.Kind == "arrow")
            {
                var angle = Math.Atan2(y2 - y, x2 - x);
                canvas.Children.Add(new Polygon { Fill = shape.Stroke, Points = new PointCollection { new(x2, y2), new(x2 - 16 * Math.Cos(angle - .45), y2 - 16 * Math.Sin(angle - .45)), new(x2 - 16 * Math.Cos(angle + .45), y2 - 16 * Math.Sin(angle + .45)) } });
            }
            if (!string.IsNullOrWhiteSpace(command.Label))
            {
                var note = new Border { Background = new SolidColorBrush(Color.FromRgb(29, 37, 51)), CornerRadius = new(7), Padding = new(10, 7, 10, 7), Child = new TextBlock { Text = command.Label, Foreground = Brushes.White, MaxWidth = 300 } };
                Canvas.SetLeft(note, Math.Clamp(x + 20, 0, Math.Max(0, ActualWidth - 300)));
                Canvas.SetTop(note, Math.Clamp(y + 22, 0, Math.Max(0, ActualHeight - 70)));
                canvas.Children.Add(note);
            }
        }
    }
    private static SolidColorBrush ParseColor(string value)
    {
        try
        {
            return new((Color)ColorConverter.ConvertFromString(value));
        }
        catch (Exception error) when (error is FormatException or NotSupportedException or InvalidOperationException)
        {
            return new(Color.FromRgb(56, 107, 255));
        }
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint h, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint h, int index, nint value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint h, nint after, int x, int y, int cx, int cy, uint flags);
}
