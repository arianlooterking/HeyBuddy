using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Markup;
using System.Xml.Linq;
using Clicky.Connectors;
using Clicky.Core;
using Clicky.Windows.Views;

internal static class Program
{
    private static int _exitCode;
    [STAThread]
    private static int Main(string[] args)
    {
        var output = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "ClickyConnectorUiChecks"));
        Directory.CreateDirectory(output);
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, Resources = LoadAppStyles() };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(async () =>
        {
            var checks = new List<string>();
            try
            {
                await RunAsync(output, checks);
            }
            catch (Exception error) { _exitCode = 1; checks.Add("FAILED: " + error); }
            await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
            {
                Passed = _exitCode == 0,
                Checks = checks
            }, new JsonSerializerOptions { WriteIndented = true }));
            foreach (var check in checks)
                Console.WriteLine(check);
            application.Shutdown(_exitCode);
            Dispatcher.ExitAllFrames();
        }));
        Dispatcher.Run();
        return _exitCode;
    }

    private static ResourceDictionary LoadAppStyles()
    {
        // A plain Application cannot accidentally start the real tray, model or owner-data services.
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ConnectorUi.AppStyles.xaml")!;
        var document = XDocument.Load(stream);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var resources = document.Root!.Element(presentation + "Application.Resources")!;
        return (ResourceDictionary)XamlReader.Parse(new XElement(presentation + "ResourceDictionary", new XAttribute(XNamespace.Xmlns + "x", xaml), resources.Nodes()).ToString());
    }

    private static async Task RunAsync(string output, List<string> checks)
    {
        var data = Path.Combine(output, "data-" + Guid.NewGuid().ToString("N"));
        var vault = Path.Combine(data, "vault");
        Directory.CreateDirectory(vault);
        await File.WriteAllTextAsync(Path.Combine(vault, "test-note.md"), "English, فارسی and Türkçe fixture.");
        var credentials = new MemoryCredentials();
        await using var service = new ConnectorService(credentials, data);
        var config = ConnectorConfiguration.FromCatalog(service.Catalog.Single(c => c.Id == "obsidian"));
        config.Name = "Personal notes · UI fixture";
        config.LocalPath = vault;
        config.Enabled = true;
        await service.SaveAsync(config);
        Require((await service.TestAsync(config.Id)).Success, "Local vault read test succeeded.", checks);
        var window = new ConnectorToolsWindow(service, service.Configurations.Single());
        window.Show();
        await Task.Delay(120);
        foreach (var size in new[] { (830, 760, "tools-desktop"), (620, 620, "tools-compact") })
        {
            window.Width = size.Item1;
            window.Height = size.Item2;
            window.UpdateLayout();
            await Task.Delay(80);
            SaveImage(window, Path.Combine(output, size.Item3 + ".png"));
            Require(window.ActualWidth >= window.MinWidth && window.ActualHeight >= window.MinHeight, "Rendered " + size.Item3, checks);
        }
        var readCheckbox = Descendants<CheckBox>(window).Single(x => AutomationProperties.GetName(x) == "Allow tool read_note");
        readCheckbox.IsChecked = false;
        Button(window, "Save access settings").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        await UntilAsync(() => service.Configurations.Single(c => c.Id == config.Id).DisabledTools.Contains("read_note"));
        Require(service.Tools.Count == 1 && service.GetConnectorTools(config.Id).Count == 2, "Disabling removes execution binding while retaining rediscoverable tool metadata.", checks);
        Require(service.GetStatus(config.Id) == ConnectorStatus.Verified, "Permission change preserves the live verified connection.", checks);
        var filter = Descendants<TextBox>(window).Single(x => AutomationProperties.GetName(x) == "Filter tools by name or description");
        filter.Text = "read_note";
        Require(Descendants<CheckBox>(window).Count(x => AutomationProperties.GetName(x).StartsWith("Allow tool ", StringComparison.Ordinal)) == 1, "Tool filter narrows visible controls.", checks);
        readCheckbox = Descendants<CheckBox>(window).Single(x => AutomationProperties.GetName(x) == "Allow tool read_note");
        readCheckbox.IsChecked = true;
        Button(window, "Save access settings").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        await UntilAsync(() => service.Configurations.Single(c => c.Id == config.Id).DisabledTools.Count == 0);
        Require(service.Tools.Count == 2, "Re-enable restores the existing reviewed binding.", checks);
        window.Close();

        var local = ConnectorConfiguration.FromCatalog(service.Catalog.Single(c => c.Id == "custom-mcp"));
        local.Name = "Protected environment · UI fixture";
        local.Transport = ConnectorTransport.Stdio;
        local.Command = "not-started-test-fixture.exe";
        await service.SaveAsync(local);
        var secretWindow = new ConnectorToolsWindow(service, local);
        secretWindow.Show();
        await Task.Delay(100);
        Button(secretWindow, "Add environment secret").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        var name = Descendants<TextBox>(secretWindow).Single(x => AutomationProperties.GetName(x) == "Environment variable name");
        var value = Descendants<PasswordBox>(secretWindow).Single();
        name.Text = "CLICKY_UI_TEST_SECRET";
        value.Password = "synthetic-credential-never-display";
        Button(secretWindow, "Save access settings").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        await UntilAsync(() => service.HasSecret(local.Id, "env.CLICKY_UI_TEST_SECRET"));
        Require(value.Password.Length == 0, "Saving clears the secret input.", checks);
        Require(!File.ReadAllText(Path.Combine(data, "connectors.json")).Contains("synthetic-credential-never-display", StringComparison.Ordinal), "Secret is absent from connector settings JSON.", checks);
        secretWindow.UpdateLayout();
        SaveImage(secretWindow, Path.Combine(output, "protected-environment.png"));
        var remove = Descendants<CheckBox>(secretWindow).Single(x => x.Content?.ToString() == "Remove this variable and its stored secret");
        remove.IsChecked = true;
        Button(secretWindow, "Save access settings").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        await UntilAsync(() => !service.HasSecret(local.Id, "env.CLICKY_UI_TEST_SECRET"));
        Require(service.Configurations.Single(c => c.Id == local.Id).SecretEnvironmentNames.Count == 0, "Removing environment access also removes its protected value.", checks);
        secretWindow.Close();
    }

    private static Button Button(Window window, string name) => Descendants<Button>(window).Single(x => x.Content?.ToString() == name);
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T value)
            yield return value;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i)))
                yield return child;
    }
    private static async Task UntilAsync(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(4))
                throw new TimeoutException("Expected UI state did not arrive.");
            await Task.Delay(30);
        }
        await Task.Delay(80);
    }
    private static void SaveImage(Window window, string path)
    {
        var surface = (FrameworkElement)window.Content;
        var width = surface.ActualWidth + surface.Margin.Left + surface.Margin.Right;
        var height = surface.ActualHeight + surface.Margin.Top + surface.Margin.Bottom;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
            drawing.DrawRectangle(new VisualBrush(surface), null, new Rect(surface.Margin.Left, surface.Margin.Top, surface.ActualWidth, surface.ActualHeight));
        }
        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
    private static void Require(bool condition, string description, List<string> checks)
    {
        if (!condition)
            throw new InvalidOperationException(description);
        checks.Add("PASS: " + description);
    }
    private sealed class MemoryCredentials : ICredentialStore
    {
        private readonly ConcurrentDictionary<string, string> _values = new();
        public string? Get(string name) => _values.GetValueOrDefault(name);
        public void Set(string name, string value) => _values[name] = value;
        public void Delete(string name) => _values.TryRemove(name, out _);
    }
}
