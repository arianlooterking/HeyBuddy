using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Clicky.Core;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace Clicky.Windows.Views;

public sealed class ApprovalWindow : Window
{
    public ApprovalWindow(ApprovalRequest request, string? targetTitle = null)
    {
        Title = "Approve this action · HeyBuddy";
        Width = 610;
        Height = 540;
        MinWidth = 450;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        var grid = new Grid { Margin = new(26) };
        grid.RowDefinitions.Add(new()
        {
            Height = GridLength.Auto
        });
        grid.RowDefinitions.Add(new()
        {
            Height = new GridLength(1, GridUnitType.Star)
        });
        grid.RowDefinitions.Add(new()
        {
            Height = GridLength.Auto
        });
        var heading = new StackPanel();
        heading.Children.Add(new TextBlock { Text = "This action needs your approval", FontSize = 23, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var actionName = request.ToolName switch
        {
            "desktop_type" => "Type into an application",
            "desktop_click" => "Click a control",
            "desktop_key" => "Press a key",
            "desktop_scroll" => "Scroll a control",
            _ => request.ToolName
        };
        heading.Children.Add(new TextBlock { Text = actionName, FontWeight = FontWeights.SemiBold, Margin = new(0, 14, 0, 7) });
        if (targetTitle is not null)
            heading.Children.Add(new TextBlock { Text = "Window: " + targetTitle, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 8) });
        heading.Children.Add(new TextBlock { Text = request.Description, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new(0, 0, 0, 15), MaxHeight = 90 });
        grid.Children.Add(heading);
        var payload = request.Arguments;
        string? preview = null;
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(payload);
            payload = System.Text.Json.JsonSerializer.Serialize(json.RootElement, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            if (request.ToolName == "desktop_type" && json.RootElement.TryGetProperty("text", out var text) && text.ValueKind == System.Text.Json.JsonValueKind.String)
                preview = text.GetString();
        }
        catch (System.Text.Json.JsonException) { }
        var details = new TextBox { Text = payload, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new("Cascadia Code, Consolas"), FontSize = 12 };
        FrameworkElement body = details;
        if (preview is not null)
        {
            var panel = new DockPanel();
            var technical = new Expander { Header = "Exact action details", Content = details, MaxHeight = 140, Margin = new(0, 10, 0, 0) };
            DockPanel.SetDock(technical, Dock.Bottom);
            panel.Children.Add(technical);
            panel.Children.Add(new TextBox { Text = preview, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FlowDirection = MainWindow.DetectLanguage(preview) == "fa" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight });
            body = panel;
        }
        Grid.SetRow(body, 1);
        grid.Children.Add(body);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new(0, 20, 0, 0) };
        var deny = new Button { Content = "Decline", IsCancel = true, Margin = new(0, 0, 10, 0) };
        deny.Click += (_, _) => { DialogResult = false; };
        row.Children.Add(deny);
        var approve = new Button { Content = "Approve this exact action", Style = (Style)System.Windows.Application.Current.FindResource("Primary") };
        approve.Click += (_, _) => { DialogResult = true; };
        row.Children.Add(approve);
        Grid.SetRow(row, 2);
        grid.Children.Add(row);
        Content = grid;
    }
}
