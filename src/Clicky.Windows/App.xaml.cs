using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Clicky.Windows;

public partial class App : System.Windows.Application
{
    private Mutex? instance;
    private Forms.NotifyIcon? tray;
    public AppServices Services { get; private set; } = null!;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--self-test", StringComparer.Ordinal))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = Diagnostics.RunAsync(e.Args, this);
            return;
        }
        instance = new Mutex(true, "Local\\ClickyLocal.Desktop", out var first);
        if (!first)
        {
            System.Windows.MessageBox.Show("HeyBuddy is already running. Open it from the system tray.", "HeyBuddy");
            Shutdown();
            return;
        }
        DispatcherUnhandledException += OnUnhandled;
        try
        {
            Services = new();
            var window = new MainWindow(Services);
            MainWindow = window;
            tray = new Forms.NotifyIcon { Text = "HeyBuddy", Icon = CreateIcon(), Visible = true };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Open HeyBuddy", null, (_, _) => window.ShowAndActivate());
            menu.Items.Add("Stop everything", null, (_, _) => window.StopAll());
            menu.Items.Add("Quit", null, async (_, _) => await QuitAsync());
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += (_, _) => window.ShowAndActivate();
            window.Show();
        }
        catch (Exception error) { System.Windows.MessageBox.Show(error.Message, "HeyBuddy could not start"); Shutdown(1); }
    }
    private static System.Drawing.Icon CreateIcon()
    {
        using var bitmap = new System.Drawing.Bitmap(32, 32);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var blue = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(56, 107, 255));
        graphics.FillPolygon(blue, [new(4, 2), new(27, 21), new(17, 23), new(12, 30)]);
        graphics.FillEllipse(System.Drawing.Brushes.White, 10, 12, 3, 4);
        graphics.FillEllipse(System.Drawing.Brushes.White, 16, 14, 3, 4);
        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = System.Drawing.Icon.FromHandle(handle);
            return (System.Drawing.Icon)temporary.Clone();
        }
        finally { DestroyIcon(handle); }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        (MainWindow as MainWindow)?.StopAll();
        System.Windows.MessageBox.Show(e.Exception.Message, "HeyBuddy stopped this operation");
    }
    public async Task QuitAsync()
    {
        (MainWindow as MainWindow)?.PrepareExit();
        tray?.Dispose();
        tray = null;
        if (Services is not null)
            await Services.DisposeAsync();
        Shutdown();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        tray?.Dispose();
        instance?.Dispose();
        base.OnExit(e);
    }
}
