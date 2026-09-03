using Clicky.Core;
using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using Forms = System.Windows.Forms;

namespace Clicky.Windows.Native;

public sealed record MonitorInfo(string Id, string Name, Rectangle Bounds, bool IsPrimary);
public sealed class ScreenCaptureService
{
    public IReadOnlyList<MonitorInfo> GetMonitors() => Forms.Screen.AllScreens.Select(s => new MonitorInfo(s.DeviceName, s.DeviceName, s.Bounds, s.Primary)).ToArray();
    public ScreenCapture CaptureForeground() => CaptureWindow(NativeMethods.GetForegroundWindow());
    public ScreenCapture CaptureMonitor(string? id = null)
    {
        var screen = Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == id) ?? Forms.Screen.PrimaryScreen ?? throw new InvalidOperationException("No display is available.");
        return CaptureRegion(screen.Bounds, screen.DeviceName);
    }
    public ScreenCapture CaptureWindow(nint hwnd)
    {
        NativeMethods.RequireSafeWindow(hwnd);
        if (!NativeMethods.GetWindowRect(hwnd, out var bounds))
            throw new InvalidOperationException("Window bounds unavailable.");
        var rectangle = Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
        if (rectangle.Width < 1 || rectangle.Height < 1 || (long)rectangle.Width * rectangle.Height > 100_000_000)
            throw new InvalidOperationException("The selected window has unsupported dimensions.");
        using var bitmap = new Bitmap(rectangle.Width, rectangle.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var deviceContext = graphics.GetHdc();
            try
            {
                // Print only this window: a screen crop could leak unrelated windows that cover it.
                if (!NativeMethods.PrintWindow(hwnd, deviceContext, 2))
                    throw new InvalidOperationException("This application does not support private window capture. Select a screen region explicitly instead.");
            }
            finally { graphics.ReleaseHdc(deviceContext); }
        }
        var nonBlack = 0;
        for (var y = 1; y <= 6; y++)
            for (var x = 1; x <= 6; x++)
            {
                var color = bitmap.GetPixel(Math.Min(rectangle.Width - 1, rectangle.Width * x / 7), Math.Min(rectangle.Height - 1, rectangle.Height * y / 7));
                if (color.R + color.G + color.B > 12)
                    nonBlack++;
            }
        if (nonBlack == 0)
            throw new InvalidOperationException("The application returned a black or protected window capture. Select a visible screen region explicitly instead.");
        using var bytes = new MemoryStream();
        bitmap.Save(bytes, ImageFormat.Png);
        return new ScreenCapture(Convert.ToBase64String(bytes.GetBuffer(), 0, (int)bytes.Length), rectangle.Width, rectangle.Height, rectangle.Left, rectangle.Top, Forms.Screen.FromHandle(hwnd).DeviceName);
    }
    public ScreenCapture CaptureRegion(Rectangle rectangle, string monitorId = "region")
    {
        var bounds = Rectangle.Intersect(rectangle, Forms.SystemInformation.VirtualScreen);
        if (bounds.Width < 1 || bounds.Height < 1 || (long)bounds.Width * bounds.Height > 100_000_000)
            throw new ArgumentOutOfRangeException(nameof(rectangle), "Select a nonempty region on an attached display.");
        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
        using var bytes = new MemoryStream();
        bitmap.Save(bytes, ImageFormat.Png);
        return new ScreenCapture(Convert.ToBase64String(bytes.GetBuffer(), 0, (int)bytes.Length), bounds.Width, bounds.Height, bounds.Left, bounds.Top, monitorId);
    }
}
