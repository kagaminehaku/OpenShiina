// The settings of the Windows player (Core Game/PlayerSettings.cs, shared with the Avalonia
// player): where the interpreter's pixel work runs, the CPU or the GPU (Vulkan). Saved at once;
// counts from the next game started.

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
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var cpu = new RadioButton
        {
            Content = "CPU", GroupName = "renderer", IsChecked = m_settings.Renderer == Renderer.Cpu, Margin = new Thickness(0, 6, 0, 0),
        };
        var gpu = new RadioButton
        {
            Content = "GPU (Vulkan)", GroupName = "renderer", IsChecked = m_settings.Renderer == Renderer.Gpu, Margin = new Thickness(0, 6, 0, 0),
        };
        cpu.Checked += (_, _) => Choose(Renderer.Cpu);
        gpu.Checked += (_, _) => Choose(Renderer.Gpu);
        var close = new Button { Content = "Close", Padding = new Thickness(16, 3, 16, 3), HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true, IsCancel = true, Margin = new Thickness(0, 16, 0, 0) };
        close.Click += (_, _) => Close();
        Content = new StackPanel
        {
            Margin = new Thickness(20, 16, 20, 16),
            Children =
            {
                new TextBlock { Text = "Drawing", FontSize = 15, FontWeight = FontWeights.SemiBold },
                new TextBlock
                {
                    Text = "Where the games' pictures are scaled and mixed. Both give the same pictures; the GPU takes the heavy work off the processor.",
                    TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 2, 0, 4),
                },
                cpu,
                gpu,
                m_device,
                new TextBlock { Text = "A change counts from the next game started.", Foreground = Brushes.DimGray, Margin = new Thickness(0, 12, 0, 0) },
                close,
            },
        };
        ShowDevice();
    }

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
