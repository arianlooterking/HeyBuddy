using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Clicky.Core;
using WpfButton = System.Windows.Controls.Button;
using WpfImage = System.Windows.Controls.Image;
using WpfColor = System.Windows.Media.Color;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfOrientation = System.Windows.Controls.Orientation;

namespace Clicky.Windows.Views;

/// <summary>Ephemeral screenshot annotation. The exported bitmap retains the original physical pixel dimensions.</summary>
public sealed class SketchWindow : Window
{
    private readonly ScreenCapture source;
    private readonly Grid imageSurface;
    private readonly InkCanvas ink;
    private readonly Stack<StrokeCollection> undo = new();
    private StrokeCollection previous = new();
    private readonly WpfButton undoButton;
    private readonly WpfButton drawButton;
    private readonly WpfButton eraseButton;
    private readonly TextBlock status;
    private bool updating;
    public ScreenCapture? Result
    {
        get; private set;
    }

    public SketchWindow(ScreenCapture capture)
    {
        if (capture.Width <= 0 || capture.Height <= 0 || (long)capture.Width * capture.Height > 100_000_000)
            throw new ArgumentException("The capture dimensions are unsupported.", nameof(capture));
        source = capture;
        Title = "Sketch on your screen · HeyBuddy";
        Width = Math.Min(1120, SystemParameters.WorkArea.Width - 48);
        Height = Math.Min(820, SystemParameters.WorkArea.Height - 48);
        MinWidth = 600;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(WpfColor.FromRgb(248, 249, 252));
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable, Segoe UI");
        FontSize = 14;
        var root = new DockPanel { Margin = new Thickness(24), LastChildFill = true };
        Content = root;

        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        heading.Children.Add(new TextBlock { Text = "Show HeyBuddy what you mean", FontSize = 24, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(WpfColor.FromRgb(29, 37, 51)) });
        heading.Children.Add(new TextBlock { Text = "Draw on this capture to point out a detail, circle a problem, or explain the next step.", Margin = new Thickness(0, 7, 0, 0), Foreground = new SolidColorBrush(WpfColor.FromRgb(89, 101, 121)), TextWrapping = TextWrapping.Wrap });
        DockPanel.SetDock(heading, Dock.Top);
        root.Children.Add(heading);

        var bitmap = new BitmapImage();
        using (var stream = new MemoryStream(Convert.FromBase64String(capture.Base64)))
        {
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
        }
        if (bitmap.PixelWidth != capture.Width || bitmap.PixelHeight != capture.Height)
            throw new ArgumentException("Capture metadata does not match its image pixels.", nameof(capture));
        imageSurface = new Grid { Width = capture.Width, Height = capture.Height, ClipToBounds = true, Background = WpfBrushes.Transparent };
        imageSurface.Children.Add(new WpfImage { Source = bitmap, Width = capture.Width, Height = capture.Height, Stretch = Stretch.Fill, IsHitTestVisible = false });
        var displayScale = Math.Max(1, capture.Width / 1000d);
        ink = new InkCanvas
        {
            Width = capture.Width,
            Height = capture.Height,
            Background = WpfBrushes.Transparent,
            EditingMode = InkCanvasEditingMode.Ink,
            EditingModeInverted = InkCanvasEditingMode.EraseByPoint,
            DefaultDrawingAttributes = new DrawingAttributes { Color = WpfColor.FromRgb(255, 72, 104), Width = 5 * displayScale, Height = 5 * displayScale, FitToCurve = true, IgnorePressure = false },
            EraserShape = new EllipseStylusShape(28 * displayScale, 28 * displayScale)
        };
        AutomationProperties.SetName(ink, "Screenshot drawing canvas");
        ink.Strokes.StrokesChanged += StrokesChanged;
        imageSurface.Children.Add(ink);

        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 14), VerticalAlignment = VerticalAlignment.Center };
        drawButton = Button("Draw", () => SetMode(InkCanvasEditingMode.Ink));
        eraseButton = Button("Erase", () => SetMode(InkCanvasEditingMode.EraseByPoint));
        undoButton = Button("Undo", Undo);
        undoButton.IsEnabled = false;
        undoButton.ToolTip = "Undo the last edit (Ctrl+Z)";
        toolbar.Children.Add(drawButton);
        toolbar.Children.Add(eraseButton);
        toolbar.Children.Add(undoButton);
        toolbar.Children.Add(Button("Clear", () => ink.Strokes.Clear()));

        var palette = new StackPanel { Orientation = WpfOrientation.Horizontal, Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        foreach (var (name, color) in new[] { ("Coral", "#FF4868"), ("Blue", "#386BFF"), ("Green", "#07875F"), ("Amber", "#FFB31A"), ("White", "#FFFFFF") })
        {
            var selected = (WpfColor)System.Windows.Media.ColorConverter.ConvertFromString(color);
            var swatch = new WpfButton { Width = 28, Height = 28, MinHeight = 28, Margin = new Thickness(3), Padding = new Thickness(0), Background = new SolidColorBrush(selected), BorderBrush = new SolidColorBrush(WpfColor.FromRgb(172, 183, 199)), BorderThickness = new Thickness(1), ToolTip = name + " pen" };
            AutomationProperties.SetName(swatch, name + " pen color");
            swatch.Click += (_, _) => { ink.DefaultDrawingAttributes.Color = selected; SetMode(InkCanvasEditingMode.Ink); };
            palette.Children.Add(swatch);
        }
        toolbar.Children.Add(palette);
        toolbar.Children.Add(new TextBlock { Text = "Width", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        var size = new Slider { Minimum = 2, Maximum = 18, Value = 5, Width = 100, VerticalAlignment = VerticalAlignment.Center, IsSnapToTickEnabled = true, TickFrequency = 1, ToolTip = "Pen thickness" };
        AutomationProperties.SetName(size, "Pen width");
        size.ValueChanged += (_, args) => { ink.DefaultDrawingAttributes.Width = args.NewValue * displayScale; ink.DefaultDrawingAttributes.Height = args.NewValue * displayScale; };
        toolbar.Children.Add(size);
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);

        var footer = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        var actions = new StackPanel { Orientation = WpfOrientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var cancel = Button("Cancel", () => { Result = null; DialogResult = false; });
        cancel.IsCancel = true;
        var accept = Button("Use annotated capture", Accept);
        accept.IsDefault = true;
        accept.MinWidth = 180;
        if (TryFindResource("Primary") is Style primary)
            accept.Style = primary;
        else
        {
            accept.Background = new SolidColorBrush(WpfColor.FromRgb(56, 107, 255));
            accept.Foreground = WpfBrushes.White;
        }
        actions.Children.Add(cancel);
        actions.Children.Add(accept);
        DockPanel.SetDock(actions, Dock.Right);
        footer.Children.Add(actions);
        status = new TextBlock { Text = "The capture and your marks stay in memory until used.", Foreground = new SolidColorBrush(WpfColor.FromRgb(89, 101, 121)), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 0) };
        footer.Children.Add(status);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        var preview = new Border { CornerRadius = new CornerRadius(12), Background = new SolidColorBrush(WpfColor.FromRgb(229, 234, 241)), Padding = new Thickness(8), BorderBrush = new SolidColorBrush(WpfColor.FromRgb(213, 221, 233)), BorderThickness = new Thickness(1), ClipToBounds = true };
        preview.Child = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both, Child = imageSurface };
        root.Children.Add(preview);
        SetMode(InkCanvasEditingMode.Ink);
        PreviewKeyDown += (_, args) => { if (args.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control) { Undo(); args.Handled = true; } };
    }
    private static WpfButton Button(string text, Action clicked)
    {
        var button = new WpfButton { Content = text, Margin = new Thickness(0, 0, 7, 0), Padding = new Thickness(13, 8, 13, 8), MinHeight = 36 };
        AutomationProperties.SetName(button, text);
        button.Click += (_, _) => clicked();
        return button;
    }
    private void SetMode(InkCanvasEditingMode mode)
    {
        ink.EditingMode = mode;
        drawButton.BorderBrush = mode == InkCanvasEditingMode.Ink ? new SolidColorBrush(WpfColor.FromRgb(56, 107, 255)) : new SolidColorBrush(WpfColor.FromRgb(223, 227, 235));
        eraseButton.BorderBrush = mode == InkCanvasEditingMode.EraseByPoint ? new SolidColorBrush(WpfColor.FromRgb(56, 107, 255)) : new SolidColorBrush(WpfColor.FromRgb(223, 227, 235));
    }
    private void StrokesChanged(object? sender, StrokeCollectionChangedEventArgs args)
    {
        if (updating)
            return;
        undo.Push(previous.Clone());
        if (undo.Count > 50)
        {
            var retained = undo.Take(50).Reverse().ToArray();
            undo.Clear();
            foreach (var item in retained)
                undo.Push(item);
        }
        previous = ink.Strokes.Clone();
        undoButton.IsEnabled = undo.Count > 0;
    }
    private void Undo()
    {
        if (!undo.TryPop(out var restored))
            return;
        updating = true;
        try
        {
            ink.Strokes.Clear();
            ink.Strokes.Add(restored);
            previous = restored.Clone();
        }
        finally { updating = false; undoButton.IsEnabled = undo.Count > 0; }
    }
    private void Accept()
    {
        try
        {
            Result = RenderCapture();
            DialogResult = true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or OutOfMemoryException) { status.Text = "Could not create the annotated capture: " + exception.Message; }
    }
    internal ScreenCapture RenderCapture()
    {
        // The image and ink share original-pixel-sized coordinates; the surrounding Viewbox is display-only.
        imageSurface.Measure(new System.Windows.Size(source.Width, source.Height));
        imageSurface.Arrange(new System.Windows.Rect(0, 0, source.Width, source.Height));
        imageSurface.UpdateLayout();
        var rendered = new RenderTargetBitmap(source.Width, source.Height, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(imageSurface);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rendered));
        using var bytes = new MemoryStream();
        encoder.Save(bytes);
        return new ScreenCapture(Convert.ToBase64String(bytes.ToArray()), source.Width, source.Height, source.Left, source.Top, source.MonitorId);
    }
}
