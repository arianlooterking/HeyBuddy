using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Clicky.Core;
using Clicky.Windows.Views;
using ShapePath = System.Windows.Shapes.Path;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var output = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/cursor-smoke");
        Directory.CreateDirectory(output);
        // No window is shown and no native input is dispatched. Saves use isolated test data.
        Environment.SetEnvironmentVariable("CLICKY_DATA_DIR", Path.Combine(output, "settings-fixture"));
        var checks = new List<object>();
        var settings = new AppSettings();
        var window = new CompanionWindow(settings, () => { });
        try
        {
            var content = (StackPanel)window.Content;
            var mascot = (Canvas)content.Children[0];
            var pointer = (ShapePath)mascot.Children[0];
            var bubble = (Border)content.Children[1];
            var status = (TextBlock)bubble.Child;
            var sizeItems = window.ContextMenu.Items.OfType<MenuItem>().Where(item => item.IsCheckable).ToArray();
            void Verify(string name, bool passed)
            {
                if (!passed)
                    throw new InvalidOperationException(name);
                checks.Add(new { name, passed });
                Console.WriteLine("PASS " + name);
            }
            Verify("Default is half size", settings.CompanionScale == .5 && sizeItems[0].IsChecked);

            Rect Bounds(double scale)
            {
                settings.CompanionScale = scale;
                window.ApplySettings();
                Layout(content);
                return pointer.TransformToAncestor(content).TransformBounds(pointer.Data.Bounds);
            }
            var normal = Bounds(1);
            var small = Bounds(.5);
            Verify("Pointer geometry is exactly half width and height", Near(small.Width * 2, normal.Width) && Near(small.Height * 2, normal.Height));
            Verify("Both eyes share the pointer transform", mascot.Children.Count == 3 && Near(mascot.LayoutTransform.Value.M11, .5) && content.LayoutTransform.Value.IsIdentity);
            Verify("Idle hit area hugs the small buddy", content.DesiredSize.Width <= 30 && content.DesiredSize.Height <= 36 && window.SizeToContent == SizeToContent.WidthAndHeight);

            foreach (var dpi in new[] { 96, 144, 192 })
            {
                Bounds(1);
                var normalPixels = Render(content, dpi, Path.Combine(output, $"normal-{dpi}dpi.png"));
                Bounds(.5);
                var smallPixels = Render(content, dpi, Path.Combine(output, $"small-{dpi}dpi.png"));
                // Raster antialiasing can extend an edge by a pixel at fractional display scales.
                Verify($"Half-size raster at {dpi} DPI", Math.Abs(smallPixels.Width * 2 - normalPixels.Width) <= 2 && Math.Abs(smallPixels.Height * 2 - normalPixels.Height) <= 2);
            }

            window.SetReply("A readable reply that keeps the same text size.");
            Bounds(1);
            var normalBubble = bubble.RenderSize;
            Bounds(.5);
            Verify("Reply bubble and 12-point text remain unscaled", bubble.RenderSize == normalBubble && status.FontSize == 12 && bubble.LayoutTransform.Value.IsIdentity);
            Render(content, 144, Path.Combine(output, "small-with-reply-144dpi.png"));

            var events = new List<double>();
            window.ScaleChanged += events.Add;
            foreach (var (index, scale) in new[] { (1, 1.0), (2, 2.0), (0, .5) })
            {
                sizeItems[index].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Layout(content);
                Verify($"Menu size {scale:0.0} saves and applies immediately", Near(settings.CompanionScale, scale) && Near(AppSettings.Load().CompanionScale, scale) && Near(mascot.LayoutTransform.Value.M11, scale) && sizeItems.Count(item => item.IsChecked) == 1 && sizeItems[index].IsChecked);
            }
            window.ApplySettings();
            Verify("Size event is raised only by menu choices", events.SequenceEqual(new[] { 1.0, 2.0, .5 }));
            Verify("Saved half size survives settings reload", AppSettings.Load().CompanionScale == .5);
            File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { passed = true, checks, smallPointerBounds = small, normalPointerBounds = normal, fixtureOnly = true, appWindowsShown = false }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"{checks.Count} cursor checks passed. Evidence: {output}");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally { window.Close(); }
    }
    private static bool Near(double first, double second) => Math.Abs(first - second) < .0001;
    private static void Layout(FrameworkElement content)
    {
        content.Measure(new(double.PositiveInfinity, double.PositiveInfinity));
        content.Arrange(new Rect(content.DesiredSize));
        content.UpdateLayout();
    }
    private static Rect Render(FrameworkElement content, int dpi, string path)
    {
        Layout(content);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.DesiredSize.Width * dpi / 96.0), (int)Math.Ceiling(content.DesiredSize.Height * dpi / 96.0), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(path))
            encoder.Save(file);
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        var left = bitmap.PixelWidth;
        var top = bitmap.PixelHeight;
        var right = -1;
        var bottom = -1;
        for (var y = 0; y < bitmap.PixelHeight; y++)
            for (var x = 0; x < bitmap.PixelWidth; x++)
                if (pixels[y * stride + x * 4 + 3] > 16)
                {
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x);
                    bottom = Math.Max(bottom, y);
                }
        if (right < left)
            throw new InvalidOperationException("Companion raster is empty.");
        return new(left, top, right - left + 1, bottom - top + 1);
    }
}

namespace Clicky.Windows
{
    // The linked companion calls this shell helper only for text direction; this fixture tests sizing.
    internal static class MainWindow
    {
        public static string DetectLanguage(string text) => "en";
    }
}
