using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Kudutree.Controls;
using Kudutree.Core;

namespace Kudutree.Views
{
    /// <summary>
    /// Shows the treemap. It's drawn on a background thread at the screen's real pixel size, again whenever the area
    /// changes size. Clicking a rectangle selects that file; the selected item is outlined.
    /// </summary>
    public sealed class TreemapView : Grid
    {
        private readonly Image _image = new Image { Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        private readonly Canvas _overlay = new Canvas { IsHitTestVisible = false, ClipToBounds = true };
        private readonly Rectangle _outerMark = new Rectangle { Stroke = Brushes.Black, StrokeThickness = 3, Visibility = Visibility.Collapsed };
        private readonly Rectangle _innerMark = new Rectangle { Stroke = Brushes.White, StrokeThickness = 1.5, Visibility = Visibility.Collapsed };
        private readonly TextBlock _message = Ui.Subtle(string.Empty, 13, false);
        private readonly DispatcherTimer _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        private readonly ToolTip _tip = new ToolTip { Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse };
        private CancellationTokenSource _cancel;
        private TreemapImage _picture;
        private double _scale = 1;
        private Node _selected;
        private Node _hover;

        public Node Root { get; private set; }
        public FileTypes Types { get; set; }
        public bool OnDisk { get; set; } = true;
        public int Highlight { get; set; } = -1;
        public TreemapImage Picture => _picture;
        public bool IsRendering => _cancel != null;

        public event Action<Node> Selected;
        public event Action<Node, FrameworkElement> MenuRequested;

        public TreemapView()
        {
            ClipToBounds = true;
            this.Res(BackgroundProperty, "SurfaceAltBg");
            Children.Add(_image);
            _overlay.Children.Add(_outerMark);
            _overlay.Children.Add(_innerMark);
            Children.Add(_overlay);
            _message.HorizontalAlignment = HorizontalAlignment.Center;
            _message.VerticalAlignment = VerticalAlignment.Center;
            Children.Add(_message);
            ToolTip = _tip;
            ToolTipService.SetInitialShowDelay(this, 250);

            _debounce.Tick += (s, e) =>
            {
                _debounce.Stop();
                Render();
            };
            SizeChanged += (s, e) => { _debounce.Stop(); _debounce.Start(); };
            MouseLeftButtonDown += (s, e) =>
            {
                var node = NodeAt(e.GetPosition(this));
                if (node == null) return;
                Select(node);
                Selected?.Invoke(node);
            };
            MouseRightButtonUp += (s, e) =>
            {
                var node = NodeAt(e.GetPosition(this));
                if (node == null) return;
                Select(node);
                Selected?.Invoke(node);
                MenuRequested?.Invoke(node, this);
                e.Handled = true;
            };
            MouseMove += (s, e) =>
            {
                var node = NodeAt(e.GetPosition(this));
                if (node == _hover) return;
                _hover = node;
                _tip.Content = node == null ? null : node.Path + "\n" + Format.Size(node.Measure(OnDisk)) + (node.IsDir ? " · " + Format.Count(node.Files, "file") : string.Empty);
                _tip.IsOpen = node != null && IsMouseOver;
            };
            MouseLeave += (s, e) => _tip.IsOpen = false;
        }

        public void Show(Node root, string message = null)
        {
            Root = root;
            _selected = null;
            _message.Text = message ?? string.Empty;
            if (message != null)
            {
                _cancel?.Cancel();
                _image.Source = null;
                _picture = null;
                Mark(null);
                return;
            }
            Render();
        }

        public void Render()
        {
            _cancel?.Cancel();
            if (Root == null || ActualWidth < 4 || ActualHeight < 4 || !string.IsNullOrEmpty(_message.Text)) return;
            var dpi = VisualTreeHelper.GetDpi(this);
            _scale = dpi.DpiScaleX;
            var width = (int)Math.Max(1, ActualWidth * dpi.DpiScaleX);
            var height = (int)Math.Max(1, ActualHeight * dpi.DpiScaleY);
            var cancel = _cancel = new CancellationTokenSource();
            var root = Root;
            var types = Types;
            var onDisk = OnDisk;
            var highlight = Highlight;
            Task.Run(() => Treemap.Render(root, width, height, onDisk, types, cancel.Token, highlight), cancel.Token).ContinueWith(t =>
            {
                if (cancel.IsCancellationRequested || t.IsFaulted || t.IsCanceled) return;
                if (_cancel == cancel) _cancel = null;
                var picture = t.Result;
                var bitmap = new WriteableBitmap(picture.Width, picture.Height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Bgr32, null);
                bitmap.WritePixels(new Int32Rect(0, 0, picture.Width, picture.Height), picture.Pixels, picture.Width * 4, 0);
                bitmap.Freeze();
                _picture = picture;
                _image.Source = bitmap;
                Mark(_selected);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private Node NodeAt(Point p) => _picture?.HitTest((int)(p.X * _scale), (int)(p.Y * _scale));

        /// <summary>Outlines a file or folder (or, if it's too small to see, the folder it's in).</summary>
        public void Select(Node node)
        {
            _selected = node;
            Mark(node);
        }

        private void Mark(Node node)
        {
            var tile = node == null ? null : _picture?.Bounds(node);
            if (tile == null)
            {
                _outerMark.Visibility = _innerMark.Visibility = Visibility.Collapsed;
                return;
            }
            var t = tile.Value;
            foreach (var mark in new[] { _outerMark, _innerMark })
            {
                Canvas.SetLeft(mark, t.X / _scale);
                Canvas.SetTop(mark, t.Y / _scale);
                mark.Width = Math.Max(3, t.W / _scale);
                mark.Height = Math.Max(3, t.H / _scale);
                mark.Visibility = Visibility.Visible;
            }
        }
    }
}
