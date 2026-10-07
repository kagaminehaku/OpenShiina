// MessageBoxA for the scripts: the text, the caption and the buttons of the type (MB_OK,
// MB_OKCANCEL, MB_YESNOCANCEL, MB_YESNO), answering with the button's Windows number (1 OK,
// 2 cancel, 6 yes, 7 no). A window of its own on the desktop, a layer over the game elsewhere.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace OpenShiina.App;

public static class MessageDialog
{
    public static async Task<int> ShowAsync(TopLevel? owner, string text, string caption, int type)
    {
        (string Label, int Result)[] buttons = (type & 0xF) switch
        {
            1 => [("OK", 1), ("Cancel", 2)],
            3 => [("Yes", 6), ("No", 7), ("Cancel", 2)],
            4 => [("Yes", 6), ("No", 7)],
            _ => [("OK", 1)],
        };
        int cancel = buttons.Any(b => b.Result == 2) ? 2 : buttons[^1].Result;
        var done = new TaskCompletionSource<int>();
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var body = new StackPanel
        {
            Spacing = 16,
            Margin = new Thickness(20),
            Children = { new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 480 }, row },
        };

        if (owner is Window window)
        {
            var dialog = new Window
            {
                Title = caption,
                Content = body,
                SizeToContent = SizeToContent.WidthAndHeight,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };
            foreach (var (label, result) in buttons)
            {
                var button = new Button { Content = label, MinWidth = 80 };
                button.Click += (_, _) => { done.TrySetResult(result); dialog.Close(); };
                row.Children.Add(button);
            }
            dialog.Closed += (_, _) => done.TrySetResult(cancel);
            await dialog.ShowDialog(window);
            return await done.Task;
        }

        // A single view (phones, tablets): a layer over the game
        var layer = OverlayLayer.GetOverlayLayer(owner as Visual ?? throw new InvalidOperationException("No view to show a message in."));
        if (layer == null)
            return cancel;
        var panel = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x20, 0x20, 0x20)),
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel { Children = { new TextBlock { Text = caption, Margin = new Thickness(20, 16, 20, 0), FontWeight = FontWeight.Bold }, body } },
        };
        foreach (var (label, result) in buttons)
        {
            var button = new Button { Content = label, MinWidth = 80 };
            button.Click += (_, _) => { done.TrySetResult(result); layer.Children.Remove(panel); };
            row.Children.Add(button);
        }
        layer.Children.Add(panel);
        return await done.Task;
    }
}
