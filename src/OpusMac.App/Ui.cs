using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace OpusMac.App;

internal static class Ui
{
    public static TextBlock Text(string text, bool secondary = false)
    {
        var control = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        if (secondary) control.Classes.Add("secondary");
        return control;
    }

    public static Button Button(string label, Action action, string? tooltip = null)
    {
        var button = new Button { Content = label };
        button.Click += (_, _) => action();
        AutomationProperties.SetName(button, label);
        if (tooltip is not null) ToolTip.SetTip(button, tooltip);
        return button;
    }

    public static Button AsyncButton(string label, Func<Task> action, Action<Exception> error, string? tooltip = null)
    {
        var button = Button(label, async () =>
        {
            try { await action(); }
            catch (OperationCanceledException) { }
            catch (Exception exception) { error(exception); }
        }, tooltip);
        return button;
    }

    public static Button IconButton(string icon, string name, Action action)
    {
        var button = Button(name, action, name);
        button.Width = 22;
        button.Padding = new Thickness(3);
        button.Content = Icon(icon, size: 14);
        return button;
    }

    public static Control Icon(string name, IBrush? color = null, double size = 16)
    {
        var geometry = name switch
        {
            "back" => "M 14,3 L 5,10 L 14,17 M 5,10 L 20,10",
            "forward" => "M 8,3 L 17,10 L 8,17 M 2,10 L 17,10",
            "up" => "M 3,12 L 10,5 L 17,12 M 10,5 L 10,20",
            "refresh" => "M 18,8 A 8,8 0 1 0 18,14 M 18,2 L 18,8 L 12,8",
            "search" => "M 10,3 A 7,7 0 1 0 10,17 A 7,7 0 1 0 10,3 M 15,15 L 21,21",
            "format-info" => "M 12,2 A 10,10 0 1 0 12,22 A 10,10 0 1 0 12,2 M 12,7 L 12,8 M 12,11 L 12,17",
            "format-lock" => "M 5,10 L 19,10 L 19,21 L 5,21 Z M 8,10 L 8,6 A 4,4 0 0 1 16,6 L 16,10 M 12,14 L 12,17",
            "home" => "M 2,10 L 11,2 L 20,10 M 5,8 L 5,21 L 17,21 L 17,8 M 9,21 L 9,14 L 13,14 L 13,21",
            "folder" => "M 2,6 L 9,6 L 12,9 L 22,9 L 22,21 L 2,21 Z",
            "file" => "M 5,2 L 14,2 L 20,8 L 20,22 L 5,22 Z M 14,2 L 14,8 L 20,8",
            "tree" => "M 2,3 L 22,3 L 22,21 L 2,21 Z M 9,3 L 9,21 M 4,7 L 7,7 M 4,11 L 7,11 M 4,15 L 7,15",
            "viewer" => "M 2,3 L 22,3 L 22,21 L 2,21 Z M 14,3 L 14,21 M 16,7 L 20,7 M 16,11 L 20,11",
            "dual" => "M 2,3 L 22,3 L 22,21 L 2,21 Z M 2,12 L 22,12",
            "vertical" => "M 2,3 L 22,3 L 22,21 L 2,21 Z M 12,3 L 12,21",
            "horizontal" => "M 2,3 L 22,3 L 22,21 L 2,21 Z M 2,12 L 22,12 M 4,6 L 10,6 M 4,15 L 10,15",
            "close" => "M 5,5 L 17,17 M 17,5 L 5,17",
            "add" => "M 11,3 L 11,19 M 3,11 L 19,11",
            "link" => "M 9,7 L 13,3 A 5,5 0 0 1 20,10 L 16,14 M 13,17 L 9,21 A 5,5 0 0 1 2,14 L 6,10 M 7,16 L 15,8",
            _ => "M 5,2 L 14,2 L 20,8 L 20,22 L 5,22 Z M 8,13 L 17,13 M 8,17 L 17,17"
        };
        var path = new ShapePath
        {
            Data = Geometry.Parse(geometry),
            StrokeThickness = 1.7, Width = size, Height = size, Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (color is null) path.Classes.Add("opus-icon");
        else path.Stroke = color;
        return path;
    }

    public static Border Surface(Control child, string css = "surface", Thickness? padding = null)
    {
        var border = new Border { Child = child, Padding = padding ?? new Thickness(0) };
        border.Classes.Add(css);
        return border;
    }

    public static DataGrid Grid(params (string Header, string Property, double Width)[] columns)
    {
        var grid = new DataGrid
        {
            AutoGenerateColumns = false, IsReadOnly = true,
            CanUserResizeColumns = true, CanUserReorderColumns = true,
            CanUserSortColumns = false, SelectionMode = DataGridSelectionMode.Extended
        };
        foreach (var column in columns)
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = column.Header, Binding = new Binding(column.Property),
                SortMemberPath = column.Property,
                Width = column.Width < 0 ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(column.Width)
            });
        return grid;
    }

    public static StackPanel Row(params Control[] controls)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        foreach (var control in controls) panel.Children.Add(control);
        return panel;
    }

    public static DataGridTemplateColumn ReadableColumn<T>(string heading, Func<T, string> value, double width = -1,
        double horizontalInset = 10, bool alignRight = false) where T : class
    {
        return new DataGridTemplateColumn
        {
            Header = heading,
            Width = width < 0 ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(width),
            CellTemplate = new FuncDataTemplate<T>((row, _) =>
            {
                var text = Text(row is null ? "" : value(row));
                text.Margin = new Thickness(horizontalInset, 0);
                text.TextTrimming = TextTrimming.CharacterEllipsis;
                if (alignRight) text.TextAlignment = TextAlignment.Right;
                ToolTip.SetTip(text, new TextBlock { Text = text.Text, MaxWidth = 760, TextWrapping = TextWrapping.Wrap });
                return text;
            })
        };
    }

    public static bool Command(Avalonia.Input.KeyModifiers modifiers) =>
        (modifiers & (Avalonia.Input.KeyModifiers.Control | Avalonia.Input.KeyModifiers.Meta)) != 0;
}
