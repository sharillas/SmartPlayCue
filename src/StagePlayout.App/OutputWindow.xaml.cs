using System.Windows;
using System.Windows.Input;
using StagePlayout.App.Video;

namespace StagePlayout.App;

/// <summary>
/// Janela de output borderless fullscreen — é o que o público vê.
/// Todo o vídeo é composto pelo nosso D3DCompositor:
/// programa A/B (crossfade) + layers L1/L2 com alpha real.
/// </summary>
public partial class OutputWindow : Window
{
    public OutputWindow()
    {
        InitializeComponent();
    }

    /// <summary>Compositor GPU (disponível após a janela carregar).</summary>
    public D3DCompositor? Compositor => CompositorHost.Compositor;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Close();
        base.OnKeyDown(e);
    }
}
