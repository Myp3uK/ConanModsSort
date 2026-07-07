using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace ConanModsSort;

public sealed class InsertionAdorner : Adorner
{
    private readonly Pen _pen;
    private readonly Brush _dot;
    private double _y = -1;

    public InsertionAdorner(UIElement adornedElement) : base(adornedElement)
    {
        IsHitTestVisible = false;
        var color = Color.FromRgb(0x5A, 0x9B, 0xF5);
        _dot = new SolidColorBrush(color);
        _dot.Freeze();
        _pen = new Pen(_dot, 2);
        _pen.Freeze();
    }

    public void SetY(double y)
    {
        _y = y;
        (Parent as AdornerLayer)?.Update(AdornedElement);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_y < 0) return;
        double w = AdornedElement.RenderSize.Width;
        dc.DrawLine(_pen, new Point(8, _y), new Point(w - 8, _y));
        dc.DrawEllipse(_dot, null, new Point(8, _y), 3.5, 3.5);
        dc.DrawEllipse(_dot, null, new Point(w - 8, _y), 3.5, 3.5);
    }
}
