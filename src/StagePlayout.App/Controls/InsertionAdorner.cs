using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace StagePlayout.App.Controls;

/// <summary>
/// Linha indicadora de inserção (azul accent) desenhada na borda do item alvo
/// durante o drag-to-reorder da playlist.
/// </summary>
public class InsertionAdorner : Adorner
{
    private static readonly Pen LinePen;
    private static readonly Brush DotBrush;

    static InsertionAdorner()
    {
        var accent = Color.FromRgb(0x3B, 0x82, 0xF6);
        LinePen = new Pen(new SolidColorBrush(accent), 3);
        LinePen.Freeze();
        DotBrush = new SolidColorBrush(accent);
        DotBrush.Freeze();
    }

    private readonly bool _below;

    public InsertionAdorner(UIElement adornedElement, bool below) : base(adornedElement)
    {
        _below = below;
        IsHitTestVisible = false;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = AdornedElement.RenderSize.Width;
        var y = _below ? AdornedElement.RenderSize.Height : 0;

        drawingContext.DrawLine(LinePen, new Point(10, y), new Point(width - 4, y));
        drawingContext.DrawEllipse(DotBrush, null, new Point(7, y), 4.5, 4.5);
    }
}
