using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using OpusMac.Core;

namespace OpusMac.App;

internal static class Dialogs
{
    public static Window Window(string title, Control content, double width = 560, double height = 280)
    {
        return new Window
        {
            Title = title, Width = width, Height = height, MinWidth = Math.Min(width, 400), MinHeight = Math.Min(height, 180),
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = content, ShowInTaskbar = false
        };
    }

    public static async Task<string?> PromptAsync(Window owner, string title, string label, string initial = "", string action = "OK")
    {
        var input = new TextBox { Text = initial };
        var caption = Ui.Text(label); caption.TextWrapping = TextWrapping.Wrap;
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 12, Children = { caption, input } };
        var dialog = Window(title, panel, 590, 210);
        var accept = Ui.Button(action, () => dialog.Close(input.Text));
        var cancel = Ui.Button("Cancel", () => dialog.Close(null));
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 7, Children = { cancel, accept } });
        input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { dialog.Close(input.Text); e.Handled = true; } };
        dialog.KeyDown += (_, e) => { if (e.Key == Key.Escape) dialog.Close(null); };
        dialog.Opened += (_, _) => { input.Focus(); input.SelectAll(); };
        return await dialog.ShowDialog<string?>(owner);
    }

    public static async Task<bool> ConfirmAsync(Window owner, string title, string message, string action = "Continue")
    {
        var text = Ui.Text(message); text.TextWrapping = TextWrapping.Wrap;
        var panel = new Grid { Margin = new Thickness(18), RowDefinitions = new RowDefinitions("*,Auto"), RowSpacing = 18 };
        panel.Children.Add(new ScrollViewer { Content = text, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        var dialog = Window(title, panel, 590, 310);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 7,
            Children = { Ui.Button("Cancel", () => dialog.Close(false)), Ui.Button(action, () => dialog.Close(true)) }
        };
        Grid.SetRow(buttons, 1); panel.Children.Add(buttons);
        dialog.KeyDown += (_, e) => { if (e.Key == Key.Escape) dialog.Close(false); };
        return await dialog.ShowDialog<bool>(owner);
    }

    public static async Task MessageAsync(Window owner, string title, string message)
    {
        var text = new TextBox { Text = message, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
        var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Margin = new Thickness(16), RowSpacing = 12 };
        grid.Children.Add(text);
        var dialog = Window(title, grid, 680, 380);
        var close = Ui.Button("Close", () => dialog.Close());
        close.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetRow(close, 1); grid.Children.Add(close);
        dialog.KeyDown += (_, e) => { if (e.Key == Key.Escape) dialog.Close(); };
        await dialog.ShowDialog(owner);
    }

    public static async Task<CollisionPolicy?> CollisionAsync(Window owner, IReadOnlyList<string> conflicts, string destination)
    {
        var text = Ui.Text($"{conflicts.Count:N0} selected item(s) already exist in:\n{destination}\n\nChoose how this transfer handles existing names. Replace is explicit and may overwrite destination contents.");
        text.TextWrapping = TextWrapping.Wrap;
        var choices = new ComboBox { ItemsSource = new[] { "Keep both (new names)", "Skip existing items", "Replace existing items" }, SelectedIndex = 0 };
        var detail = new TextBox { Text = string.Join(Environment.NewLine, conflicts.Take(30).Select(Path.GetFileName)), IsReadOnly = true, AcceptsReturn = true, Height = 105 };
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 12, Children = { text, detail, choices } };
        var dialog = Window("Existing files", panel, 650, 390);
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 7,
            Children =
            {
                Ui.Button("Cancel", () => dialog.Close(null)),
                Ui.Button("Continue", () => dialog.Close((CollisionPolicy?)(choices.SelectedIndex switch { 1 => CollisionPolicy.Skip, 2 => CollisionPolicy.Replace, _ => CollisionPolicy.KeepBoth })))
            }
        });
        return await dialog.ShowDialog<CollisionPolicy?>(owner);
    }

    public static Grid Form(params (string Label, Control Control)[] fields)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowSpacing = 8, ColumnSpacing = 12 };
        for (var index = 0; index < fields.Length; index++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = Ui.Text(fields[index].Label);
            Grid.SetRow(label, index); grid.Children.Add(label);
            Grid.SetRow(fields[index].Control, index); Grid.SetColumn(fields[index].Control, 1); grid.Children.Add(fields[index].Control);
        }
        return grid;
    }
}
