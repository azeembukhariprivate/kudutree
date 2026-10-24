using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Kudutree.Controls
{
    /// <summary>The KUDUTREE mark: a folder tree, with branches of different lengths (like sizes). Uses the text colour.</summary>
    public class LogoMark : Viewbox
    {
        public LogoMark(double size = 40)
        {
            Width = size;
            Height = size;
            Focusable = false;
            IsHitTestVisible = false;

            var canvas = new Canvas { Width = 64, Height = 64 };
            var lines = new Path
            {
                Data = Geometry.Parse("M20,22 V46 H30 M20,32 H30"),
                StrokeThickness = 4,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round
            };
            Bind(lines, Shape.StrokeProperty);
            canvas.Children.Add(lines);
            foreach (var (x, y, w) in new[] { (12.0, 12.0, 17.0), (30.0, 26.5, 22.0), (30.0, 40.5, 14.0) })
            {
                var box = new Rectangle { Width = w, Height = 11, RadiusX = 3, RadiusY = 3 };
                Canvas.SetLeft(box, x);
                Canvas.SetTop(box, y);
                Bind(box, Shape.FillProperty);
                canvas.Children.Add(box);
            }
            Child = canvas;
        }

        private void Bind(Shape shape, DependencyProperty property) =>
            shape.SetBinding(property, new Binding { Path = new PropertyPath(TextElement.ForegroundProperty), Source = this });
    }

    /// <summary>The app tile: the mark in white on a green rounded square.</summary>
    public static class LogoTile
    {
        public static Border Create(double size)
        {
            var mark = new LogoMark(size) ;
            mark.SetValue(TextElement.ForegroundProperty, Brushes.White);
            return new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(size * 0.234),
                Background = new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x43)),
                Child = mark
            };
        }
    }
}
