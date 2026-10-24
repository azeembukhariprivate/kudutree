using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Kudutree.Core
{
    /// <summary>A rectangle in the treemap, in pixels.</summary>
    public struct Tile
    {
        public int X, Y, W, H;
        public Node Node;

        public bool Contains(int x, int y) => x >= X && y >= Y && x < X + W && y < Y + H;
    }

    public sealed class TreemapImage
    {
        public int Width, Height;
        /// <summary>BGRA pixels, row by row.</summary>
        public int[] Pixels;
        /// <summary>Every drawn folder and file, parents before what's inside them.</summary>
        public List<Tile> Tiles = new List<Tile>();
        private Dictionary<Node, int> _index;

        /// <summary>The deepest item at a point (a file, or a folder too small to show what's inside).</summary>
        public Node HitTest(int x, int y)
        {
            for (var i = Tiles.Count - 1; i >= 0; i--)
                if (Tiles[i].Contains(x, y)) return Tiles[i].Node;
            return null;
        }

        /// <summary>Where a file or folder is drawn; for items too small to be drawn, the nearest folder that is.</summary>
        public Tile? Bounds(Node node)
        {
            if (_index == null)
            {
                _index = new Dictionary<Node, int>(Tiles.Count);
                for (var i = 0; i < Tiles.Count; i++) _index[Tiles[i].Node] = i;
            }
            for (var n = node; n != null; n = n.Parent)
                if (_index.TryGetValue(n, out var i)) return Tiles[i];
            return null;
        }
    }

    /// <summary>
    /// The treemap: every file is a rectangle with an area matching the space it takes, inside rectangles for its folders,
    /// laid out with the "squarified" method (rectangles as close to squares as possible) and shaded as cushions so the
    /// folder structure stands out, in the way SequoiaView and WinDirStat draw it.
    /// </summary>
    public static class Treemap
    {
        public static readonly int[] Palette =
        {
            0x3B82F6, 0xEF4444, 0x22C55E, 0xEAB308, 0x06B6D4, 0xD946EF, 0xF97316, 0x8B5CF6, 0x14B8A6, 0xEC4899, 0x84CC16
        };
        public const int Grey = 0xA3A3A3;
        public const int FolderGrey = 0x8A8A8A;

        // Cushion shading (van Wijk and van de Wetering), with WinDirStat's usual settings.
        private const double Height = 0.38, ScaleFactor = 0.91, Ambient = 0.15, Diffuse = 1 - Ambient, Gain = 1.12;
        private static readonly double Lx, Ly, Lz;

        static Treemap()
        {
            var len = Math.Sqrt(1 + 1 + 100);
            Lx = -1 / len;
            Ly = -1 / len;
            Lz = 10 / len;
        }

        public static int ColorOf(Node node, FileTypes types)
        {
            if (node.IsDir) return FolderGrey;
            var t = types?.Get(node.Type);
            return t == null || t.Color < 0 ? Grey : Palette[t.Color % Palette.Length];
        }

        public static TreemapImage Render(Node root, int width, int height, bool onDisk, FileTypes types, CancellationToken token = default)
        {
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            var image = new TreemapImage { Width = width, Height = height, Pixels = new int[width * height] };
            for (var i = 0; i < image.Pixels.Length; i++) image.Pixels[i] = unchecked((int)0xFF000000) | 0x303030;
            if (root.Measure(onDisk) <= 0) return image;
            Draw(image, root, 0, 0, width, height, new double[4], Height, true, onDisk, types, token);
            return image;
        }

        private static void Draw(TreemapImage image, Node node, double x, double y, double w, double h, double[] surface, double ridge,
            bool asRoot, bool onDisk, FileTypes types, CancellationToken token)
        {
            int x0 = (int)Math.Round(x), y0 = (int)Math.Round(y), x1 = (int)Math.Round(x + w), y1 = (int)Math.Round(y + h);
            if (x1 <= x0 || y1 <= y0) return;
            var s = (double[])surface.Clone();
            if (!asRoot) AddRidge(x, y, w, h, s, ridge);
            image.Tiles.Add(new Tile { X = x0, Y = y0, W = x1 - x0, H = y1 - y0, Node = node });

            List<Node> kids = null;
            if (node.IsDir && node.Has(NodeFlags.Listed) && (x1 - x0) * (y1 - y0) >= 4)
                kids = node.Children.Where(c => !c.Has(NodeFlags.Deleted) && c.Measure(onDisk) > 0).OrderByDescending(c => c.Measure(onDisk)).ToList();
            if (kids == null || kids.Count == 0)
            {
                Fill(image, x0, y0, x1, y1, s, ColorOf(node, types));
                return;
            }
            if (image.Tiles.Count % 4096 == 0) token.ThrowIfCancellationRequested();
            foreach (var (child, r) in Layout(kids.Select(k => (double)k.Measure(onDisk)).ToList(), x, y, w, h).Select((r, i) => (kids[i], r)))
                Draw(image, child, r.X, r.Y, r.W, r.H, s, ridge * ScaleFactor, false, onDisk, types, token);
        }

        public struct Rect
        {
            public double X, Y, W, H;
        }

        /// <summary>
        /// Squarified layout: sizes (largest first) are placed in rows along the shorter side of the space left, adding
        /// items to a row while that keeps its rectangles closer to squares.
        /// </summary>
        public static List<Rect> Layout(IList<double> sizes, double x, double y, double w, double h)
        {
            var result = new List<Rect>(sizes.Count);
            var remaining = sizes.Sum();
            var i = 0;
            while (i < sizes.Count)
            {
                if (remaining <= 0 || w <= 0 || h <= 0)
                {
                    for (; i < sizes.Count; i++) result.Add(new Rect { X = x, Y = y });
                    break;
                }
                var wide = w >= h;
                var shortSide = wide ? h : w;
                var longSide = wide ? w : h;

                var j = i;
                double rowSum = 0, best = double.MaxValue;
                while (j < sizes.Count)
                {
                    var sum = rowSum + sizes[j];
                    var thick = sum / remaining * longSide;
                    if (thick <= 0)
                    {
                        rowSum = sum;
                        j++;
                        continue;
                    }
                    var biggest = sizes[i] / sum * shortSide;
                    var smallest = sizes[j] / sum * shortSide;
                    var worst = Math.Max(Ratio(thick, biggest), Ratio(thick, smallest));
                    if (worst > best) break;
                    best = worst;
                    rowSum = sum;
                    j++;
                }
                if (j == i) j = i + 1;
                if (rowSum == 0) rowSum = sizes[i];

                var rowThick = j >= sizes.Count ? longSide : rowSum / remaining * longSide;
                var along = 0.0;
                for (var k = i; k < j; k++)
                {
                    var len = k == j - 1 ? shortSide - along : sizes[k] / rowSum * shortSide;
                    result.Add(wide
                        ? new Rect { X = x, Y = y + along, W = rowThick, H = len }
                        : new Rect { X = x + along, Y = y, W = len, H = rowThick });
                    along += len;
                }
                if (wide)
                {
                    x += rowThick;
                    w -= rowThick;
                }
                else
                {
                    y += rowThick;
                    h -= rowThick;
                }
                remaining -= rowSum;
                i = j;
            }
            return result;
        }

        private static double Ratio(double a, double b) => a <= 0 || b <= 0 ? double.MaxValue : Math.Max(a / b, b / a);

        private static void AddRidge(double x, double y, double w, double h, double[] s, double ridge)
        {
            var h4 = 4 * ridge;
            var wf = h4 / w;
            s[2] += wf * (x + x + w);
            s[0] -= wf;
            var hf = h4 / h;
            s[3] += hf * (y + y + h);
            s[1] -= hf;
        }

        private static void Fill(TreemapImage image, int x0, int y0, int x1, int y1, double[] s, int rgb)
        {
            double r = (rgb >> 16) & 0xFF, g = (rgb >> 8) & 0xFF, b = rgb & 0xFF;
            x1 = Math.Min(x1, image.Width);
            y1 = Math.Min(y1, image.Height);
            for (var iy = Math.Max(0, y0); iy < y1; iy++)
            {
                var ny = -(2 * s[1] * (iy + 0.5) + s[3]);
                var row = iy * image.Width;
                for (var ix = Math.Max(0, x0); ix < x1; ix++)
                {
                    var nx = -(2 * s[0] * (ix + 0.5) + s[2]);
                    var cos = (nx * Lx + ny * Ly + Lz) / Math.Sqrt(nx * nx + ny * ny + 1);
                    if (cos > 1) cos = 1;
                    var light = Diffuse * cos;
                    if (light < 0) light = 0;
                    light = (light + Ambient) * Gain;
                    int pr = Clamp(r * light), pg = Clamp(g * light), pb = Clamp(b * light);
                    image.Pixels[row + ix] = unchecked((int)0xFF000000) | (pr << 16) | (pg << 8) | pb;
                }
            }
        }

        private static int Clamp(double v) => v < 0 ? 0 : v > 255 ? 255 : (int)v;
    }
}
