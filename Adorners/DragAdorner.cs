using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ConanModsSort;

public sealed class DragAdorner : Adorner
{
    private readonly Rectangle _shape;
    private double _left, _top;

    public DragAdorner(UIElement adornedElement, UIElement dragged)
        : base(adornedElement)
    {
        IsHitTestVisible = false;
        var brush = new VisualBrush(dragged) { Opacity = 0.9, Stretch = Stretch.None };
        _shape = new Rectangle
        {
            Width = dragged.RenderSize.Width,
            Height = dragged.RenderSize.Height,
            Fill = brush,
            RadiusX = 6,
            RadiusY = 6,
            Stroke = new SolidColorBrush(Color.FromRgb(0x5A, 0x9B, 0xF5)),
            StrokeThickness = 1.5,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black, BlurRadius = 12, ShadowDepth = 0, Opacity = 0.6
            }
        };
    }

    public void SetPosition(double left, double top)
    {
        _left = left;
        _top = top;
        (Parent as AdornerLayer)?.Update(AdornedElement);
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _shape;

    protected override Size MeasureOverride(Size constraint)
    {
        _shape.Measure(constraint);
        return _shape.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _shape.Arrange(new Rect(new Point(_left + 8, _top + 8), _shape.DesiredSize));
        return finalSize;
    }
}
