using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;
using WpfButton = System.Windows.Controls.Button;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace Clicky.Windows;

public partial class MainWindow
{
    private WpfTextBox CompanionColorField()
    {
        var input = Field("Companion color", app.Settings.CompanionColor);
        input.ToolTip = "Enter a hex color such as #386BFF, or choose any custom RGB color from the palette.";
        var swatch = new Border { Width = 38, Height = 34, CornerRadius = new(7), BorderThickness = new(1), BorderBrush = (Brush)FindResource("Line"), Margin = new(0, 0, 10, 0) };
        void RefreshSwatch()
        {
            if (TryCompanionColor(app.Settings.CompanionColor, out var current))
                swatch.Background = new SolidColorBrush(current);
        }
        RefreshSwatch();
        var palette = new WpfButton { Content = "Choose from color palette…", ToolTip = "Full color palette with custom red, green and blue values" };
        System.Windows.Automation.AutomationProperties.SetName(palette, "Choose companion color from full palette");
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 5, 0, 0) };
        row.Children.Add(swatch);
        row.Children.Add(palette);
        PageContent.Children.Add(row);
        Note("Choose any color from the full palette or enter its hex value. Valid color changes save and update the companion immediately.");
        input.TextChanged += (_, _) =>
        {
            if (TryCompanionColor(input.Text, out var color) && ApplyCompanionColor(color))
                RefreshSwatch();
        };
        palette.Click += (_, _) => ChooseCompanionColor(input, ShowCompanionColorDialog);
        return input;
    }

    private void ChooseCompanionColor(WpfTextBox input, Func<Color, Color?> choose)
    {
        if (!TryCompanionColor(app.Settings.CompanionColor, out var previous))
            previous = Color.FromRgb(56, 107, 255);
        var selected = choose(previous);
        if (selected is null)
            return;
        // Updating the field runs the same live persistence and preview path as typing hex.
        input.Text = CompanionColorText(selected.Value);
    }

    private Color? ShowCompanionColorDialog(Color current)
    {
        using var dialog = CreateCompanionColorDialog(current);
        return dialog.ShowDialog(new ColorDialogOwner(new WindowInteropHelper(this).Handle)) == Forms.DialogResult.OK
            ? Color.FromRgb(dialog.Color.R, dialog.Color.G, dialog.Color.B)
            : null;
    }

    private static Forms.ColorDialog CreateCompanionColorDialog(Color current) =>
        new()
        {
            FullOpen = true,
            AnyColor = true,
            AllowFullOpen = true,
            Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B)
        };

    private bool ApplyCompanionColor(Color color)
    {
        var value = CompanionColorText(color);
        if (string.Equals(app.Settings.CompanionColor, value, StringComparison.OrdinalIgnoreCase))
            return true;
        var previous = app.Settings.CompanionColor;
        try
        {
            app.Settings.CompanionColor = value;
            app.Settings.Save();
            companion?.ApplySettings();
            SetStatus("Companion color saved and applied.");
            return true;
        }
        catch (Exception error)
        {
            app.Settings.CompanionColor = previous;
            companion?.ApplySettings();
            SetStatus("Could not save the companion color: " + error.Message);
            return false;
        }
    }

    private static string CompanionColorText(Color color) => color.A == 255 ? $"#{color.R:X2}{color.G:X2}{color.B:X2}" : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    private static bool TryCompanionColor(string value, out Color color)
    {
        color = default;
        var text = value.Trim();
        // A partially typed hex value must not change the saved appearance.
        if (text.Length == 0 || text.StartsWith('#') && text.Length is not (7 or 9))
            return false;
        try
        {
            if (ColorConverter.ConvertFromString(text) is not Color parsed)
                return false;
            color = parsed;
            return true;
        }
        catch (Exception error) when (error is FormatException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    private sealed class ColorDialogOwner(nint handle) : Forms.IWin32Window
    {
        public nint Handle { get; } = handle;
    }
}
