// The settings of the Windows player (Core Game/PlayerSettings.cs, shared with the Avalonia
// player): where the interpreter's pixel work runs (the CPU or the GPU through Vulkan), then the
// on / off settings (PlayerSettings.Switches) in their sections. Saved at once; each counts from
// the next game started.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OpenShiina.Windows;

public sealed class SettingsWindow : Window
{
    private readonly PlayerSettings m_settings = PlayerSettings.Load();
    private readonly TextBlock m_device = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(20, 2, 0, 0) };

    public SettingsWindow()
    {
        Title = "Settings";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var page = new StackPanel { Margin = new Thickness(20, 12, 20, 16) };
        page.Children.Add(Heading("Drawing"));
        page.Children.Add(Help("Where the games' pictures are scaled and mixed. Both give the same pictures; the GPU takes the heavy work off the processor."));
        var cpu = new RadioButton { Content = "CPU", GroupName = "renderer", IsChecked = m_settings.Renderer == Renderer.Cpu, Margin = new Thickness(0, 6, 0, 0) };
        var gpu = new RadioButton { Content = "GPU (Vulkan)", GroupName = "renderer", IsChecked = m_settings.Renderer == Renderer.Gpu, Margin = new Thickness(0, 6, 0, 0) };
        cpu.Checked += (_, _) => Choose(Renderer.Cpu);
        gpu.Checked += (_, _) => Choose(Renderer.Gpu);
        page.Children.Add(cpu);
        page.Children.Add(gpu);
        page.Children.Add(m_device);

        string? section = null;
        foreach (var item in PlayerSettings.Switches)
        {
            if (item.Section != section)
            {
                section = item.Section;
                page.Children.Add(Heading(section));
            }
            var box = new CheckBox { Content = item.Label, IsChecked = item.Get(m_settings), Margin = new Thickness(0, 6, 0, 0) };
            box.Click += (_, _) =>
            {
                item.Set(m_settings, box.IsChecked == true);
                m_settings.Save();
            };
            page.Children.Add(box);
            page.Children.Add(new TextBlock { Text = item.Help, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(20, 0, 0, 0) });
        }

        page.Children.Add(new TextBlock { Text = "A change counts from the next game started.", Foreground = Brushes.DimGray, Margin = new Thickness(0, 14, 0, 0) });
        var close = new Button { Content = "Close", Padding = new Thickness(16, 3, 16, 3), HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true, IsCancel = true, Margin = new Thickness(0, 12, 0, 0) };
        close.Click += (_, _) => Close();
        page.Children.Add(close);
        Content = page;
        ShowDevice();
    }

    private static TextBlock Heading(string text) =>
        new() { Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 0) };

    private static TextBlock Help(string text) =>
        new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 2, 0, 4) };

    private void Choose(Renderer renderer)
    {
        if (m_settings.Renderer == renderer)
            return;
        m_settings.Renderer = renderer;
        m_settings.Save();
        ShowDevice();
    }

    private void ShowDevice()
    {
        if (m_settings.Renderer != Renderer.Gpu)
        {
            m_device.Text = "";
            return;
        }
        m_device.Text = PlayerSettings.Gpu(out string? error) is { } gpu
            ? gpu.Name
            : $"No GPU to use ({error}): the games run on the CPU.";
    }
}
