using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Clicky.Connectors;
using Clicky.Core;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;

namespace Clicky.Windows.Views;

/// <summary>Editable, persisted tool access and protected stdio environment for one saved connection.</summary>
public sealed class ConnectorToolsWindow : Window
{
    private readonly ConnectorService _service;
    private readonly string _id;
    private readonly StackPanel _toolList = new();
    private readonly StackPanel _environmentList = new();
    private readonly TextBox _filter = new();
    private readonly TextBlock _snapshot = new();
    private readonly TextBlock _feedback = new();
    private readonly TextBlock _count = new();
    private readonly Dictionary<string, bool> _enabled = new(StringComparer.Ordinal);
    private readonly List<EnvironmentRow> _environment = [];
    private readonly List<Button> _operationButtons = [];
    private readonly List<(string OriginalName, ToolDefinition Definition)> _knownTools = [];
    private readonly CancellationTokenSource _lifetime;
    private CancellationTokenSource? _operation;
    private bool _closed;
    private bool _busy;
    private bool _dirty;
    private readonly bool _stdio;
    private sealed record EnvironmentRow(TextBox Name, PasswordBox Value, CheckBox Remove, TextBlock State, Border Container, string OriginalName);

    public ConnectorToolsWindow(ConnectorService service, ConnectorConfiguration configuration, CancellationToken cancellationToken = default)
    {
        _service = service;
        _id = configuration.Id;
        _stdio = configuration.Transport == ConnectorTransport.Stdio;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Title = configuration.Name + " · Tool access";
        Width = 830;
        Height = 760;
        MinWidth = 620;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("#F8F9FC");
        Foreground = Brush("#1D2533");
        FontFamily = new FontFamily("Segoe UI Variable, Segoe UI");
        FontSize = 14;

        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = configuration.Name, FontSize = 26, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = "Choose which tools HeyBuddy can request. Sensitive actions still require confirmation.", Foreground = Brush("#596579"), Margin = new Thickness(0, 7, 0, 15), TextWrapping = TextWrapping.Wrap });
        _snapshot.TextWrapping = TextWrapping.Wrap;
        _snapshot.FontSize = 12;
        _snapshot.Foreground = Brush("#596579");
        _snapshot.Margin = new Thickness(0, 0, 0, 14);
        header.Children.Add(_snapshot);
        root.Children.Add(header);

        var body = new StackPanel();
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 0, 0, 12) };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        body.Children.Add(Heading("Available tools"));
        _filter.MinHeight = 36;
        _filter.Padding = new Thickness(10, 7, 10, 7);
        _filter.Margin = new Thickness(0, 5, 10, 10);
        AutomationProperties.SetName(_filter, "Filter tools by name or description");
        _filter.ToolTip = "Filter tools by name or description";
        _filter.TextChanged += (_, _) => RenderTools();
        body.Children.Add(_filter);
        var toolActions = new WrapPanel { Margin = new Thickness(0, 0, 0, 7) };
        toolActions.Children.Add(Action("Enable shown", () => { SetVisibleEnabled(true); return Task.CompletedTask; }));
        toolActions.Children.Add(Action("Disable shown", () => { SetVisibleEnabled(false); return Task.CompletedTask; }));
        toolActions.Children.Add(Action("Refresh discovery", async () =>
        {
            await _service.RefreshToolsAsync(_id, _operation!.Token);
            LoadDiscovered(preserveEdits: true);
            SetFeedback("Tool schemas refreshed. Unsaved choices are preserved.");
        }));
        body.Children.Add(toolActions);
        _count.Foreground = Brush("#596579");
        _count.FontSize = 12;
        _count.Margin = new Thickness(0, 0, 0, 9);
        body.Children.Add(_count);
        body.Children.Add(_toolList);

        if (_stdio)
        {
            body.Children.Add(Heading("Protected environment"));
            body.Children.Add(new TextBlock
            {
                Text = "Only these named secrets are passed to the local MCP process, alongside a minimal Windows environment. Existing values stay hidden; leave the password field blank to preserve a saved value. Saving environment changes disconnects the process until you test it again.",
                Foreground = Brush("#596579"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 10, 12),
                FontSize = 12
            });
            body.Children.Add(_environmentList);
            foreach (var name in configuration.SecretEnvironmentNames)
                AddEnvironment(name);
            body.Children.Add(Action("Add environment secret", () => { AddEnvironment(""); _dirty = true; return Task.CompletedTask; }));
        }

        var footer = new StackPanel();
        _feedback.TextWrapping = TextWrapping.Wrap;
        _feedback.Foreground = Brush("#596579");
        _feedback.Margin = new Thickness(0, 3, 0, 10);
        AutomationProperties.SetLiveSetting(_feedback, AutomationLiveSetting.Polite);
        footer.Children.Add(_feedback);
        var actions = new WrapPanel();
        var save = Action("Save access settings", SaveChangesAsync);
        save.Background = Brush("#386BFF");
        save.Foreground = Brushes.White;
        save.BorderBrush = Brush("#386BFF");
        actions.Children.Add(save);
        actions.Children.Add(Action("Save and test connection", async () =>
        {
            await SaveChangesAsync();
            SetFeedback("Testing connection. OAuth may open your browser; no account write is performed by this test.");
            var result = await _service.TestAsync(_id, _operation!.Token);
            LoadDiscovered(preserveEdits: false);
            SetFeedback(result.Status + ": " + result.Message, !result.Success);
        }));
        var cancel = new Button { Content = "Cancel current check", Margin = new Thickness(0, 0, 8, 7), Padding = new Thickness(12, 8, 12, 8) };
        cancel.Click += (_, _) => { _operation?.Cancel(); SetFeedback("Cancelling the current check…"); };
        actions.Children.Add(cancel);
        var close = new Button { Content = "Close", Margin = new Thickness(0, 0, 0, 7), Padding = new Thickness(12, 8, 12, 8), IsCancel = true };
        close.Click += (_, _) => Close();
        actions.Children.Add(close);
        footer.Children.Add(actions);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        Content = root;

        Closing += (_, args) =>
        {
            if (_dirty && !_busy && System.Windows.MessageBox.Show(this, "Close without saving these access settings?", "Unsaved connection settings", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                args.Cancel = true;
                return;
            }
            _closed = true;
            _lifetime.Cancel();
            _operation?.Cancel();
        };
        Closed += (_, _) => { _service.Changed -= OnServiceChanged; _lifetime.Dispose(); };
        _service.Changed += OnServiceChanged;
        LoadDiscovered(preserveEdits: false);
        UpdateSnapshot();
        SetFeedback("Tool changes take effect when saved. Disabling a tool also prevents queued requests that have not started.");
    }

    private Button Action(string title, Func<Task> action)
    {
        var button = new Button { Content = title, Margin = new Thickness(0, 0, 8, 7), Padding = new Thickness(12, 8, 12, 8), MinHeight = 36 };
        AutomationProperties.SetName(button, title);
        _operationButtons.Add(button);
        button.Click += async (_, _) =>
        {
            if (_busy || _closed)
                return;
            _busy = true;
            foreach (var control in _operationButtons)
                control.IsEnabled = false;
            _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            try
            {
                await action();
            }
            catch (OperationCanceledException) { SetFeedback("Check cancelled. Completed changes remain saved."); }
            catch (Exception error) { SetFeedback(error.Message, true); }
            finally
            {
                _operation.Dispose();
                _operation = null;
                _busy = false;
                if (!_closed)
                {
                    foreach (var control in _operationButtons)
                        control.IsEnabled = true;
                    UpdateSnapshot();
                }
            }
        };
        return button;
    }

    private void LoadDiscovered(bool preserveEdits)
    {
        var config = Current();
        _knownTools.Clear();
        _knownTools.AddRange(_service.GetConnectorTools(_id));
        if (!preserveEdits)
            _enabled.Clear();
        foreach (var tool in _knownTools)
            _enabled.TryAdd(tool.OriginalName, !config.DisabledTools.Contains(tool.OriginalName));
        foreach (var disabled in config.DisabledTools)
            _enabled.TryAdd(disabled, false);
        RenderTools();
    }

    private void RenderTools()
    {
        _toolList.Children.Clear();
        var shown = VisibleTools().ToArray();
        _count.Text = $"{shown.Length} shown · {_knownTools.Count} discovered · {_enabled.Count(p => !p.Value)} disabled";
        if (_knownTools.Count == 0)
            _toolList.Children.Add(new TextBlock { Text = "No tool schemas have been discovered in this session. Save and test the connection to inspect its tools. Previously disabled tool names remain listed below.", TextWrapping = TextWrapping.Wrap, Foreground = Brush("#596579"), Margin = new Thickness(0, 5, 10, 14) });
        foreach (var tool in shown)
        {
            var container = new StackPanel();
            var heading = new DockPanel();
            var risk = new TextBlock
            {
                Text = tool.Definition.Risk == RiskLevel.ReadOnly ? "READ" : "CONFIRM EACH ACTION",
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush(tool.Definition.Risk == RiskLevel.ReadOnly ? "#296144" : "#8A4913"),
                Margin = new Thickness(12, 8, 0, 0),
                VerticalAlignment = VerticalAlignment.Top
            };
            DockPanel.SetDock(risk, Dock.Right);
            heading.Children.Add(risk);
            var check = new CheckBox { Content = new TextBlock { Text = tool.OriginalName, TextWrapping = TextWrapping.Wrap }, IsChecked = _enabled.GetValueOrDefault(tool.OriginalName, true), FontWeight = FontWeights.SemiBold, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 0, 6) };
            AutomationProperties.SetName(check, "Allow tool " + tool.OriginalName);
            check.Checked += (_, _) => { _enabled[tool.OriginalName] = true; _dirty = true; };
            check.Unchecked += (_, _) => { _enabled[tool.OriginalName] = false; _dirty = true; };
            heading.Children.Add(check);
            container.Children.Add(heading);
            var details = new Expander { Header = "Description and input fields", Foreground = Brush("#596579"), FontSize = 12 };
            details.Content = new TextBox
            {
                Text = tool.Definition.Description + "\n\n" + JsonSerializer.Serialize(tool.Definition.InputSchema, new JsonSerializerOptions { WriteIndented = true }),
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                MaxHeight = 230,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                Margin = new Thickness(0, 9, 0, 4),
                Background = Brushes.White,
                BorderThickness = new Thickness(0)
            };
            container.Children.Add(details);
            _toolList.Children.Add(new Border { Child = container, Background = Brushes.White, BorderBrush = Brush("#DFE3EB"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 9, 14, 11), Margin = new Thickness(0, 0, 10, 8) });
        }
        foreach (var entry in _enabled.Where(p => !_knownTools.Any(t => t.OriginalName == p.Key) && p.Key.Contains(_filter.Text.Trim(), StringComparison.OrdinalIgnoreCase)).OrderBy(p => p.Key))
        {
            var check = new CheckBox { Content = new TextBlock { Text = entry.Key + " · saved rule; schema not currently discovered", TextWrapping = TextWrapping.Wrap }, IsChecked = entry.Value, Margin = new Thickness(0, 5, 10, 7) };
            AutomationProperties.SetName(check, "Allow previously discovered tool " + entry.Key);
            check.Checked += (_, _) => { _enabled[entry.Key] = true; _dirty = true; };
            check.Unchecked += (_, _) => { _enabled[entry.Key] = false; _dirty = true; };
            _toolList.Children.Add(check);
        }
    }

    private IEnumerable<(string OriginalName, ToolDefinition Definition)> VisibleTools()
    {
        var text = _filter.Text.Trim();
        return _knownTools.Where(t => text.Length == 0 || t.OriginalName.Contains(text, StringComparison.OrdinalIgnoreCase) || t.Definition.Description.Contains(text, StringComparison.OrdinalIgnoreCase));
    }
    private void SetVisibleEnabled(bool enabled)
    {
        foreach (var tool in VisibleTools())
            _enabled[tool.OriginalName] = enabled;
        _dirty = true;
        RenderTools();
    }

    private void AddEnvironment(string name)
    {
        var panel = new StackPanel();
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.42, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.58, GridUnitType.Star) });
        var nameBox = new TextBox { Text = name, MinHeight = 36, Padding = new Thickness(9, 7, 9, 7), Margin = new Thickness(0, 0, 10, 0) };
        AutomationProperties.SetName(nameBox, "Environment variable name");
        nameBox.ToolTip = "For example GITHUB_TOKEN; use the exact variable name required by this local server.";
        grid.Children.Add(nameBox);
        var value = new PasswordBox { MinHeight = 36, Padding = new Thickness(9, 7, 9, 7) };
        AutomationProperties.SetName(value, "New secret value for " + (name.Length == 0 ? "environment variable" : name));
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
        panel.Children.Add(grid);
        var state = new TextBlock { Text = name.Length > 0 && _service.HasSecret(_id, "env." + name) ? "Saved secret exists. Leave the value blank to keep it." : "Enter a value before this local server can start.", FontSize = 11, Foreground = Brush("#596579"), Margin = new Thickness(0, 7, 0, 2) };
        panel.Children.Add(state);
        var remove = new CheckBox { Content = "Remove this variable and its stored secret", Margin = new Thickness(0, 4, 0, 0), FontSize = 12 };
        panel.Children.Add(remove);
        var container = new Border { Child = panel, Background = Brushes.White, BorderBrush = Brush("#DFE3EB"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(14), Margin = new Thickness(0, 0, 10, 10) };
        _environment.Add(new(nameBox, value, remove, state, container, name));
        _environmentList.Children.Add(container);
        nameBox.TextChanged += (_, _) => _dirty = true;
        value.PasswordChanged += (_, _) => _dirty = true;
        remove.Checked += (_, _) => { _dirty = true; nameBox.IsEnabled = false; value.IsEnabled = false; };
        remove.Unchecked += (_, _) => { _dirty = true; nameBox.IsEnabled = true; value.IsEnabled = true; };
    }

    private async Task SaveChangesAsync()
    {
        var config = Current(); // Preserve editor changes made elsewhere while this window was open.
        var disabled = _enabled.Where(p => !p.Value).Select(p => p.Key).Distinct(StringComparer.Ordinal).ToList();
        var rows = _environment.Where(r => r.Remove.IsChecked != true).ToArray();
        var names = rows.Select(r => r.Name.Text.Trim()).ToArray();
        if (_stdio)
        {
            if (names.Any(n => !Regex.IsMatch(n, "^[A-Za-z_][A-Za-z0-9_]{0,127}$")))
                throw new ArgumentException("Each environment variable needs a valid name using letters, digits and underscores.");
            if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length)
                throw new ArgumentException("Environment variable names must be unique.");
            foreach (var row in rows)
                if (row.Value.Password.Length == 0 && !_service.HasSecret(_id, "env." + row.Name.Text.Trim()))
                    throw new ArgumentException("Enter the secret for " + row.Name.Text.Trim() + " before saving.");
        }
        var environmentChanged = _stdio && (!config.SecretEnvironmentNames.SequenceEqual(names, StringComparer.Ordinal)
            || _environment.Any(r => r.Value.Password.Length > 0 || r.Remove.IsChecked == true));
        if (environmentChanged)
        {
            var removed = config.SecretEnvironmentNames.Except(names, StringComparer.Ordinal).ToArray();
            config.SecretEnvironmentNames = [.. names];
            config.DisabledTools = disabled;
            await _service.SaveAsync(config, _operation!.Token);
            foreach (var name in removed)
                _service.SetSecret(_id, "env." + name, "");
            foreach (var row in rows)
            {
                if (row.Value.Password.Length > 0)
                    _service.SetSecret(_id, "env." + row.Name.Text.Trim(), row.Value.Password);
                row.Value.Clear();
                row.State.Text = "Saved secret exists. Leave the value blank to keep it.";
            }
            foreach (var row in _environment.Where(r => r.Remove.IsChecked == true).ToArray())
            {
                _environmentList.Children.Remove(row.Container);
                _environment.Remove(row);
            }
        }
        else
            await _service.SetToolAccessAsync(_id, disabled, _operation!.Token);
        _dirty = false;
        UpdateSnapshot();
        SetFeedback(environmentChanged ? "Access settings and protected environment saved. The local process was disconnected; test the connection to restart it." : "Tool access saved. The new permissions apply to future and queued requests.");
    }

    private ConnectorConfiguration Current() => _service.Configurations.SingleOrDefault(c => c.Id == _id)
        ?? throw new InvalidOperationException("Save the connection in its setup editor before opening tool access.");
    private void OnServiceChanged()
    {
        if (!_closed)
            Dispatcher.BeginInvoke(new System.Action(UpdateSnapshot));
    }
    private void UpdateSnapshot()
    {
        if (_closed)
            return;
        var config = Current();
        _snapshot.Text = $"{_service.GetStatus(_id)}  ·  {(string.IsNullOrWhiteSpace(config.Account) ? "Account identity not verified" : config.Account)}\n"
            + $"Last successful read: {(config.LastVerifiedAt is null ? "Not yet verified" : config.LastVerifiedAt.Value.ToLocalTime().ToString("g"))}\n"
            + "Configured scopes: " + (config.Scopes.Count == 0 ? "Provider discovery / no explicit scopes" : string.Join(", ", config.Scopes));
    }
    private void SetFeedback(string text, bool error = false)
    {
        if (_closed)
            return;
        _feedback.Text = text;
        _feedback.Foreground = Brush(error ? "#A02C37" : "#596579");
    }
    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 8) };
    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
}
