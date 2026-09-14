using System.Windows;

namespace StagePlayout.App;

/// <summary>
/// Overlay de confiança no ecrã de palco: mostra o cue atual + tempo restante.
/// Janela transparente, always-on-top, sem foco e transparente ao rato
/// (posicionada por cima da OutputWindow).
/// </summary>
public partial class OverlayWindow : Window
{
    public OverlayWindow()
    {
        InitializeComponent();
    }

    public void SetText(string text)
    {
        if (OvlText.Text != text)
            OvlText.Text = text;
    }
}
