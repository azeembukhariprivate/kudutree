using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Kudutree.Controls;
using Kudutree.Core;

namespace Kudutree.Views
{
    public enum SortBy { Size, Name, Files, Modified }

    /// <summary>
    /// The folder tree, shown as a flat list of rows (only the rows on screen are built, so huge folders stay quick).
    /// Folders open with a click on the arrow, a double-click, or the right arrow key. A big folder shows its 500 biggest
    /// items and a row for the rest, which shows more when clicked.
    /// </summary>
    public sealed class TreeList
    {
        private const int Page = 500;

        private readonly RowList<TreeRow> _rows = new RowList<TreeRow>();
        private readonly HashSet<Node> _expanded = new HashSet<Node>();
        private readonly Dictionary<Node, int> _shown = new Dictionary<Node, int>();
        private Node _root;

        public ListBox List { get; }
        public SortBy Sort { get; private set; } = SortBy.Size;
        public bool Descending { get; private set; } = true;

        public event Action<Node> Selected;
        public event Action<Node> Activated;
        public event Action<Node, FrameworkElement> MenuRequested;

        public TreeList()
        {
            List = new ListBox { SelectionMode = SelectionMode.Single, ItemsSource = _rows, ItemTemplate = Templates.Tree }.Styled("TableList");
            VirtualizingPanel.SetIsVirtualizing(List, true);
            VirtualizingPanel.SetVirtualizationMode(List, VirtualizationMode.Recycling);
            ScrollViewer.SetCanContentScroll(List, true);
            List.SelectionChanged += (s, e) =>
            {
                if (List.SelectedItem is TreeRow row && !row.IsMore) Selected?.Invoke(row.Node);
            };
            List.PreviewMouseLeftButtonDown += (s, e) =>
            {
                var row = RowAt(e.OriginalSource);
                if (row == null) return;
                if (row.IsMore)
                {
                    ShowMore(row);
                    e.Handled = true;
                    return;
                }
                if (e.OriginalSource is FrameworkElement fe && fe.Name == "Exp")
                {
                    Toggle(row);
                    e.Handled = true;
                }
                else if (e.ClickCount == 2)
                {
                    if (row.Node.IsDir) Toggle(row);
                    else Activated?.Invoke(row.Node);
                    e.Handled = true;
                }
            };
            List.PreviewMouseRightButtonDown += (s, e) =>
            {
                var row = RowAt(e.OriginalSource);
                if (row == null || row.IsMore) return;
                List.SelectedItem = row;
                MenuRequested?.Invoke(row.Node, List);
                e.Handled = true;
            };
            List.PreviewKeyDown += (s, e) =>
            {
                if (!(List.SelectedItem is TreeRow row) || row.IsMore) return;
                if (e.Key == Key.Right && row.Node.IsDir && !row.Expanded) { Toggle(row); e.Handled = true; }
                else if (e.Key == Key.Left)
                {
                    if (row.Expanded) Toggle(row);
                    else if (row.Node.Parent != null && row.Node != _root) Reveal(row.Node.Parent);
                    e.Handled = true;
                }
                else if (e.Key == Key.Enter) { Activated?.Invoke(row.Node); e.Handled = true; }
            };
        }

        private static TreeRow RowAt(object source)
        {
            for (var d = source as DependencyObject; d != null; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
                if (d is FrameworkElement fe && fe.DataContext is TreeRow row) return row;
            return null;
        }

        public IReadOnlyList<TreeRow> Rows => _rows;
        public Node Root => _root;

        public Node SelectedNode => (List.SelectedItem as TreeRow)?.Node;

        public void SetRoot(Node root)
        {
            _root = root;
            _expanded.Clear();
            _shown.Clear();
            _expanded.Add(root);
            Rebuild();
            if (_rows.Count > 0) List.SelectedIndex = 0;
        }

        public void SetSort(SortBy sort)
        {
            if (Sort == sort) Descending = !Descending;
            else
            {
                Sort = sort;
                Descending = sort != SortBy.Name;
            }
            Rebuild();
        }

        /// <summary>Rebuilds the rows from the tree, keeping open folders open and the selection (sizes may have changed).</summary>
        public void Rebuild()
        {
            var selected = SelectedNode;
            var rows = new List<TreeRow>();
            if (_root != null) Add(rows, _root, 0);
            _rows.Replace(rows);
            if (selected != null)
            {
                var row = rows.FirstOrDefault(r => r.Node == selected);
                if (row != null) List.SelectedItem = row;
            }
        }

        /// <summary>Updates what the rows show (sizes while scanning) without rebuilding them.</summary>
        public void RefreshValues()
        {
            foreach (var r in _rows) r.Refresh();
        }

        private void Add(List<TreeRow> rows, Node node, int depth)
        {
            var open = node.IsDir && node.Has(NodeFlags.Listed) && _expanded.Contains(node);
            rows.Add(new TreeRow { Node = node, Depth = depth, Expanded = open });
            if (!open) return;
            var kids = Sorted(node);
            var shown = _shown.TryGetValue(node, out var n) ? n : Page;
            foreach (var child in kids.Take(shown)) Add(rows, child, depth + 1);
            if (kids.Count > shown)
            {
                var rest = kids.Skip(shown).ToList();
                rows.Add(new TreeRow { MoreOf = node, Depth = depth + 1, MoreCount = rest.Count, MoreSize = rest.Sum(k => k.Measure(RowBase.OnDisk)) });
            }
        }

        private List<Node> Sorted(Node folder)
        {
            var kids = folder.Children.Where(c => !c.Has(NodeFlags.Deleted)).ToList();
            IOrderedEnumerable<Node> ordered;
            switch (Sort)
            {
                case SortBy.Name:
                    ordered = Descending ? kids.OrderByDescending(k => k.Name, StringComparer.CurrentCultureIgnoreCase) : kids.OrderBy(k => k.Name, StringComparer.CurrentCultureIgnoreCase);
                    break;
                case SortBy.Files:
                    ordered = Descending ? kids.OrderByDescending(k => k.Files) : kids.OrderBy(k => k.Files);
                    break;
                case SortBy.Modified:
                    ordered = Descending ? kids.OrderByDescending(k => k.Mtime) : kids.OrderBy(k => k.Mtime);
                    break;
                default:
                    ordered = Descending ? kids.OrderByDescending(k => k.Measure(RowBase.OnDisk)) : kids.OrderBy(k => k.Measure(RowBase.OnDisk));
                    break;
            }
            return ordered.ThenBy(k => k.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        public void Toggle(TreeRow row)
        {
            if (row.IsMore || !row.Node.IsDir || !row.Node.Has(NodeFlags.Listed) || row.Node.Has(NodeFlags.Link)) return;
            if (!_expanded.Remove(row.Node)) _expanded.Add(row.Node);
            var index = _rows.IndexOf(row);
            Rebuild();
            if (index >= 0 && index < _rows.Count) List.SelectedIndex = index;
        }

        public void Expand(Node node)
        {
            if (node.IsDir && _expanded.Add(node)) Rebuild();
        }

        private void ShowMore(TreeRow row)
        {
            _shown[row.MoreOf] = (_shown.TryGetValue(row.MoreOf, out var n) ? n : Page) + Page;
            Rebuild();
        }

        /// <summary>Opens the folders above a file or folder and selects it.</summary>
        public void Reveal(Node node)
        {
            if (node == null || _root == null || !node.IsInside(_root)) return;
            for (var p = node.Parent; p != null && p.IsInside(_root); p = p.Parent) _expanded.Add(p);
            // Make sure it isn't hidden in a big folder's "more" row.
            for (var n = node; n != _root && n.Parent != null; n = n.Parent)
            {
                var kids = Sorted(n.Parent);
                var at = kids.IndexOf(n);
                var shown = _shown.TryGetValue(n.Parent, out var s) ? s : Page;
                if (at >= shown) _shown[n.Parent] = (at / Page + 1) * Page;
            }
            Rebuild();
            var row = _rows.FirstOrDefault(r => r.Node == node);
            if (row == null) return;
            List.SelectedItem = row;
            List.ScrollIntoView(row);
        }
    }
}
