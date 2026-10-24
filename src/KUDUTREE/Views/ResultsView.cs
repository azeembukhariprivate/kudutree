using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Kudutree.Controls;
using Kudutree.Core;

namespace Kudutree.Views
{
    /// <summary>
    /// The results of a scan: the folder tree (or the largest files, or a search) on the left, file types on the right and
    /// the treemap below. Everything follows the same selection. Built once per window and filled for each scan.
    /// </summary>
    public sealed class ResultsView : Grid
    {
        private readonly MainWindow _owner;
        private readonly TreeList _tree = new TreeList();
        private readonly TreemapView _treemap = new TreemapView();
        private readonly RowList<ItemRow> _largestRows = new RowList<ItemRow>();
        private readonly RowList<ItemRow> _searchRows = new RowList<ItemRow>();
        private readonly RowList<TypeRow> _typeRows = new RowList<TypeRow>();
        private readonly ListBox _largest;
        private readonly ListBox _search;
        private readonly ListBox _types;
        private readonly ContentControl _tab = new ContentControl { Focusable = false };
        private readonly Dictionary<string, RadioButton> _tabs = new Dictionary<string, RadioButton>();
        private readonly TextBlock _place = Ui.Text(string.Empty, 14, bold: true);
        private readonly TextBlock _summary = Ui.Subtle(string.Empty, 12, false);
        private readonly ContentControl _banner = new ContentControl { Focusable = false };
        private readonly Button _up;
        private readonly RadioButton _onDisk;
        private readonly RadioButton _fileSize;
        private readonly CheckBox _showMap = new CheckBox { Content = "Treemap", VerticalAlignment = VerticalAlignment.Center };
        private readonly RowDefinition _mapRow = new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 80 };
        private readonly RowDefinition _splitRow = new RowDefinition { Height = new GridLength(8) };
        private readonly TextBox _query = new TextBox { MinHeight = 32 };
        private readonly ComboBox _minSize = new ComboBox { MinWidth = 120 };
        private readonly ComboBox _changed = new ComboBox { MinWidth = 160 };
        private readonly ComboBox _kind = new ComboBox { MinWidth = 120 };
        private readonly TextBlock _searchStatus = Ui.Subtle(string.Empty, 12, false);
        private readonly TextBlock _typesNote = Ui.Subtle("Click a type to show only its files in the treemap.", 11.5);
        private CancellationTokenSource _searchCancel;
        private bool _live;

        private static readonly (string Text, long Bytes)[] SizeChoices = { ("Any size", 0), ("1 MB or more", 1L << 20), ("10 MB or more", 10L << 20), ("100 MB or more", 100L << 20), ("1 GB or more", 1L << 30) };
        private static readonly (string Text, int Days, bool Older)[] ChangeChoices = { ("Any date", 0, false), ("Changed in the last 7 days", 7, false), ("Changed in the last 30 days", 30, false), ("Not changed for a year", 365, true), ("Not changed for 3 years", 3 * 365, true) };

        public ScanResult Scan { get; private set; }
        public Node ViewRoot { get; private set; }
        public Node Selected { get; private set; }
        public TreeList Tree => _tree;
        public TreemapView Map => _treemap;
        public IReadOnlyList<ItemRow> LargestRows => _largestRows;
        public IReadOnlyList<ItemRow> SearchRows => _searchRows;
        public IReadOnlyList<TypeRow> TypeRows => _typeRows;
        public string CurrentTab { get; private set; } = "Folders";

        public ResultsView(MainWindow owner)
        {
            _owner = owner;
            Margin = new Thickness(16, 12, 16, 12);
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.4, GridUnitType.Star), MinHeight = 160 });
            RowDefinitions.Add(_splitRow);
            RowDefinitions.Add(_mapRow);

            // ---- toolbar
            var toolbar = Ui.Columns(Ui.Auto, Ui.Star(), Ui.Auto, Ui.Auto, Ui.Auto);
            _up = Ui.Small(Ui.Button("Up", "IconUp", "SecondaryButton", (s, e) => { if (ViewRoot?.Parent != null) SetViewRoot(ViewRoot.Parent); }));
            _up.ToolTip = "Show the folder above";
            var where = Ui.Column(_place, _summary);
            toolbar.Children.Add(_up.Margin(0, 0, 12, 0).At(0, 0));
            toolbar.Children.Add(where.At(0, 1));
            _onDisk = new RadioButton { Content = "Size on disk", GroupName = "measure", IsChecked = Settings.SizeOnDisk, ToolTip = "Space used on the drive: compressed, sparse and online-only files take less; hard links count once" }.Styled("SegmentRadio");
            _fileSize = new RadioButton { Content = "File size", GroupName = "measure", IsChecked = !Settings.SizeOnDisk, ToolTip = "The sizes File Explorer shows" }.Styled("SegmentRadio");
            _onDisk.BorderThickness = new Thickness(1, 1, 0, 1);
            _fileSize.BorderThickness = new Thickness(1);
            _onDisk.Checked += (s, e) => SetMeasure(true);
            _fileSize.Checked += (s, e) => SetMeasure(false);
            toolbar.Children.Add(Ui.Row(_onDisk, _fileSize).Margin(12, 0, 12, 0).At(0, 2));
            _showMap.IsChecked = Settings.ShowTreemap;
            _showMap.Click += (s, e) => SetMapVisible(_showMap.IsChecked == true);
            toolbar.Children.Add(_showMap.Margin(0, 0, 12, 0).At(0, 3));
            toolbar.Children.Add(Ui.Small(Ui.Button("Scan again", "IconRefresh", "SecondaryButton", (s, e) => _owner.Rescan())).At(0, 4));
            Children.Add(toolbar.Margin(0, 0, 0, 8).At(0));
            Children.Add(_banner.At(1));

            // ---- left: tabs
            var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            foreach (var name in new[] { "Folders", "Largest files", "Search" })
            {
                var tab = new RadioButton { Content = name, GroupName = "tabs", IsChecked = name == "Folders" }.Styled("TabRadio");
                tab.Checked += (s, e) => ShowTab(name);
                _tabs[name] = tab;
                tabs.Children.Add(tab);
            }
            var left = new Grid();
            left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            left.RowDefinitions.Add(new RowDefinition());
            left.Children.Add(tabs.At(0));
            left.Children.Add(_tab.At(1));

            _largest = ItemList(_largestRows);
            _search = ItemList(_searchRows);
            _types = new ListBox { ItemsSource = _typeRows, ItemTemplate = Templates.Type }.Styled("TableList");
            _types.SelectionChanged += (s, e) =>
            {
                var type = (_types.SelectedItem as TypeRow)?.Type;
                _treemap.Highlight = type?.Index ?? -1;
                _typesNote.Text = type == null ? "Click a type to show only its files in the treemap." : $"The treemap shows only {type.Title} files. Click it again to show everything.";
                _treemap.Render();
            };
            _types.PreviewMouseLeftButtonDown += (s, e) =>
            {
                for (var d = e.OriginalSource as DependencyObject; d != null; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
                    if (d is ListBoxItem item && item.IsSelected)
                    {
                        _types.SelectedItem = null;
                        e.Handled = true;
                        return;
                    }
            };

            var right = new Grid();
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            right.RowDefinitions.Add(new RowDefinition());
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            right.Children.Add(Ui.Heading("File types", 14).Margin(4, 4, 0, 8).At(0));
            right.Children.Add(_types.At(1));
            right.Children.Add(_typesNote.Margin(4, 8, 0, 0).At(2));

            var middle = new Grid();
            middle.ColumnDefinitions.Add(new ColumnDefinition { MinWidth = 400 });
            middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300), MinWidth = 200 });
            middle.Children.Add(Ui.Card(left, 12).At(0, 0));
            middle.Children.Add(new GridSplitter { Width = 8, HorizontalAlignment = HorizontalAlignment.Stretch, Background = System.Windows.Media.Brushes.Transparent }.At(0, 1));
            middle.Children.Add(Ui.Card(right, 12).At(0, 2));
            Children.Add(middle.At(2));
            Children.Add(new GridSplitter { Height = 8, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, Background = System.Windows.Media.Brushes.Transparent, ResizeDirection = GridResizeDirection.Rows }.At(3));
            var mapCard = new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = _treemap }.Styled("Card");
            mapCard.Padding = new Thickness(0);
            Children.Add(mapCard.At(4));

            // ---- events
            _tree.Selected += n => Select(n, _tree);
            _tree.Activated += Actions.Open;
            _tree.MenuRequested += (n, at) => ShowMenu(n, at);
            _treemap.Selected += n =>
            {
                ShowTab("Folders");
                Select(n, _treemap);
            };
            _treemap.MenuRequested += (n, at) => ShowMenu(n, at);
            BuildSearch();
            SetMapVisible(Settings.ShowTreemap);
            ShowTab("Folders");
        }

        private ListBox ItemList(RowList<ItemRow> rows)
        {
            var list = new ListBox { ItemsSource = rows, ItemTemplate = Templates.Item }.Styled("TableList");
            VirtualizingPanel.SetIsVirtualizing(list, true);
            VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
            list.SelectionChanged += (s, e) =>
            {
                if (list.SelectedItem is ItemRow row) Select(row.Node, list);
            };
            list.MouseDoubleClick += (s, e) =>
            {
                if (list.SelectedItem is ItemRow row) Actions.Open(row.Node);
            };
            list.PreviewMouseRightButtonUp += (s, e) =>
            {
                for (var d = e.OriginalSource as DependencyObject; d != null; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
                    if (d is ListBoxItem item && item.DataContext is ItemRow row)
                    {
                        list.SelectedItem = row;
                        ShowMenu(row.Node, list);
                        e.Handled = true;
                        return;
                    }
            };
            list.PreviewKeyDown += (s, e) => KeyAction(e, (list.SelectedItem as ItemRow)?.Node);
            return list;
        }

        private Grid _treePage;
        private Grid _treeHeader;
        private UIElement _largestPage;
        private TextBlock _largestNote;
        private UIElement _searchPage;

        private UIElement TreePage()
        {
            if (_treePage == null)
            {
                _treeHeader = Ui.Columns(Templates.TreeColumns);
                _treeHeader.Margin = new Thickness(4, 0, 18, 4);
                _treePage = new Grid();
                _treePage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                _treePage.RowDefinitions.Add(new RowDefinition());
                _treePage.Children.Add(_treeHeader.At(0));
                _treePage.Children.Add(_tree.List.At(1));
                _tree.List.PreviewKeyDown += TreeKeys;
            }
            FillTreeHeader();
            return _treePage;
        }

        private void FillTreeHeader()
        {
            _treeHeader.Children.Clear();
            void Head(string text, int column, SortBy sort, HorizontalAlignment align)
            {
                var arrow = _tree.Sort == sort ? (_tree.Descending ? " ▾" : " ▴") : string.Empty;
                var b = Ui.Link(text + arrow, (s, e) =>
                {
                    _tree.SetSort(sort);
                    FillTreeHeader();
                });
                b.HorizontalAlignment = align;
                b.Margin = new Thickness(column == 0 ? 18 : 0, 0, column == 1 || column == 3 ? 12 : 0, 0);
                _treeHeader.Children.Add(b.At(0, column));
            }
            Head("Name", 0, SortBy.Name, HorizontalAlignment.Left);
            Head("Size", 1, SortBy.Size, HorizontalAlignment.Right);
            _treeHeader.Children.Add(Ui.Subtle("Share of folder", 12, false).At(0, 2));
            Head("Files", 3, SortBy.Files, HorizontalAlignment.Right);
            Head("Modified", 4, SortBy.Modified, HorizontalAlignment.Left);
        }

        private void TreeKeys(object sender, KeyEventArgs e) => KeyAction(e, _tree.SelectedNode);

        private void KeyAction(KeyEventArgs e, Node node)
        {
            if (node == null) return;
            if (e.Key == Key.Delete)
            {
                _owner.DeleteNode(node, (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
                e.Handled = true;
            }
            else if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                Actions.CopyPath(node);
                e.Handled = true;
            }
        }

        private UIElement ListPage(ListBox list, TextBlock note)
        {
            var header = Ui.Columns(Ui.Star(), Ui.Px(100), Ui.Px(90), Ui.Px(100));
            header.Margin = new Thickness(28, 0, 18, 4);
            header.Children.Add(Ui.Subtle("Name and folder", 12, false).At(0, 0));
            var size = Ui.Subtle("Size", 12, false);
            size.HorizontalAlignment = HorizontalAlignment.Right;
            size.Margin = new Thickness(0, 0, 12, 0);
            header.Children.Add(size.At(0, 1));
            header.Children.Add(Ui.Subtle("Modified", 12, false).At(0, 3));
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition());
            if (note != null) grid.Children.Add(note.Margin(4, 0, 0, 8).At(0));
            grid.Children.Add(header.At(1));
            grid.Children.Add(list.At(2));
            return grid;
        }

        private void BuildSearch()
        {
            foreach (var c in SizeChoices) _minSize.Items.Add(c.Text);
            foreach (var c in ChangeChoices) _changed.Items.Add(c.Text);
            foreach (var k in new[] { "Files and folders", "Files", "Folders" }) _kind.Items.Add(k);
            _minSize.SelectedIndex = 0;
            _changed.SelectedIndex = 0;
            _kind.SelectedIndex = 0;
            _query.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) RunSearch();
            };
        }

        private UIElement SearchPage()
        {
            if (_searchPage != null) return _searchPage;
            var row = Ui.Columns(Ui.Star(), Ui.Auto);
            row.Children.Add(_query.At(0, 0));
            row.Children.Add(Ui.Button("Search", "IconSearch", "PrimaryButton", (s, e) => RunSearch()).Margin(8, 0, 0, 0).At(0, 1));
            var filters = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            filters.Children.Add(_minSize.Margin(0, 0, 8, 0));
            filters.Children.Add(_changed.Margin(0, 0, 8, 0));
            filters.Children.Add(_kind);
            var top = Ui.Column(
                Ui.Subtle("Part of a name, or a pattern like *.iso or IMG_????.jpg (separate several with ;). Leave it empty to use only the filters.", 12),
                row.Margin(0, 6, 0, 0), filters, _searchStatus.Margin(0, 8, 0, 0));
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition());
            grid.Children.Add(top.Margin(4, 0, 0, 8).At(0));
            grid.Children.Add(ListPage(_search, null).At(1));
            return _searchPage = grid;
        }

        public void ShowTab(string name)
        {
            CurrentTab = name;
            if (_tabs.TryGetValue(name, out var tab) && tab.IsChecked != true) tab.IsChecked = true;
            switch (name)
            {
                case "Largest files":
                    if (_largestRows.Count == 0 && Scan != null && !_live) FillLargest();
                    if (_largestPage == null)
                    {
                        _largestNote = Ui.Subtle(string.Empty, 12);
                        _largestPage = ListPage(_largest, _largestNote);
                    }
                    _largestNote.Text = _live ? "The largest files are listed when the scan finishes." : $"The {_largestRows.Count:N0} biggest files in {ViewRoot?.Path}.";
                    _tab.Content = _largestPage;
                    break;
                case "Search":
                    _tab.Content = SearchPage();
                    Dispatcher.BeginInvoke(new Action(() => _query.Focus()));
                    break;
                default:
                    _tab.Content = TreePage();
                    break;
            }
        }

        // ------------------------------------------------------------------ filling

        /// <summary>Shows a scan. While a folder listing is still running (<paramref name="live"/>), the tree grows as it goes.</summary>
        public void Show(ScanResult scan, bool live)
        {
            Scan = scan;
            _live = live;
            RowBase.OnDisk = Settings.SizeOnDisk;
            RowBase.Types = scan.Types;
            _searchRows.Replace(Enumerable.Empty<ItemRow>());
            _searchStatus.Text = string.Empty;
            SetViewRoot(scan.Root);
        }

        /// <summary>The scan has finished: fill the lists that need everything, and draw the treemap.</summary>
        public void Finished()
        {
            _live = false;
            RowBase.Types = Scan.Types;
            _tree.Rebuild();
            _tree.RefreshValues();
            RefreshAll();
        }

        public void LiveTick()
        {
            if (!_live) return;
            _tree.Rebuild();
            _tree.RefreshValues();
            UpdateSummary();
        }

        public void SetViewRoot(Node root)
        {
            ViewRoot = root;
            _up.IsEnabled = root.Parent != null;
            _tree.SetRoot(root);
            _largestRows.Replace(Enumerable.Empty<ItemRow>());
            RefreshAll();
            Select(root, null);
        }

        private void RefreshAll()
        {
            UpdateSummary();
            if (_live)
            {
                _typeRows.Replace(Enumerable.Empty<TypeRow>());
                _treemap.Show(ViewRoot, "The treemap appears when the scan finishes.");
                if (CurrentTab == "Largest files") ShowTab(CurrentTab);
                return;
            }
            FillTypes();
            if (CurrentTab == "Largest files") FillLargest();
            else _largestRows.Replace(Enumerable.Empty<ItemRow>());
            if (CurrentTab != "Folders") ShowTab(CurrentTab);
            foreach (var r in _searchRows.ToList()) if (r.Node.Has(NodeFlags.Deleted)) _searchRows.Remove(r);
            foreach (var r in _searchRows) r.Refresh();
            _treemap.Types = Scan.Types;
            _treemap.OnDisk = RowBase.OnDisk;
            if (Settings.ShowTreemap) _treemap.Show(ViewRoot);
        }

        private void FillTypes()
        {
            var total = Scan.Types.All.Sum(t => t.Measure(RowBase.OnDisk));
            var selected = (_types.SelectedItem as TypeRow)?.Type;
            _typeRows.Replace(Scan.Types.All.Where(t => t.Count > 0).OrderByDescending(t => t.Measure(RowBase.OnDisk)).Select(t => new TypeRow { Type = t, Total = total }));
            if (selected != null) _types.SelectedItem = _typeRows.FirstOrDefault(r => r.Type == selected);
        }

        private void FillLargest()
        {
            _largestRows.Replace(Queries.Largest(ViewRoot, 1000, RowBase.OnDisk).Select(n => new ItemRow { Node = n }));
        }

        private void UpdateSummary()
        {
            if (Scan == null) return;
            var root = ViewRoot ?? Scan.Root;
            _place.Text = root.Path;
            var text = $"{Format.Count(root.Files, "file")} in {Format.Count(root.Folders, "folder")} · {Format.Size(root.Alloc)} on disk · {Format.Size(root.Size)} in files";
            if (!_live) text += $" · {Scan.Method.ToLowerInvariant()}, {Format.Duration(Scan.Duration)}";
            _summary.Text = text;

            _banner.Content = null;
            if (_live || root != Scan.Root) return;
            var notes = new List<string>();
            if (Scan.DriveTotal > 0)
            {
                var used = Scan.DriveTotal - Scan.DriveFree;
                var missing = used - Scan.Root.Alloc;
                if (missing > Math.Max(used / 50, 512L << 20))
                    notes.Add($"The drive has {Format.Size(used)} in use, but the scan found {Format.Size(Scan.Root.Alloc)}. The other {Format.Size(missing)} is in folders that couldn't be read" +
                              (Scan.Fast ? " or in Windows' own system space (restore points, the page file's reserve)." : " (without administrator rights) or in Windows' own system space."));
            }
            if (Scan.Unreadable.Count > 0)
                notes.Add($"{Format.Count(Scan.Unreadable.Count, "folder")} couldn't be read (marked in the tree).");
            if (notes.Count == 0) return;
            var body = Ui.Column(Ui.Text(string.Join(" ", notes), 12.5, wrap: true));
            if (!Scan.Fast && Paths.IsDriveRoot(Scan.Path) && _owner.CanFastScan(Scan.Path))
                body.Children.Add(Ui.Link("Use the fast scan instead (reads the drive's file index, counts everything; asks for administrator rights)", (s, e) => _owner.StartScan(Scan.Path, true)).Margin(0, 4, 0, 0));
            _banner.Content = Ui.Banner("IconInfo", body).Margin(0, 0, 0, 8);
        }

        private void SetMeasure(bool onDisk)
        {
            if (Settings.SizeOnDisk == onDisk && RowBase.OnDisk == onDisk) return;
            Settings.SizeOnDisk = onDisk;
            RowBase.OnDisk = onDisk;
            if (Scan == null) return;
            _tree.Rebuild();
            _tree.RefreshValues();
            RefreshAll();
        }

        public void SetMapVisible(bool visible)
        {
            Settings.ShowTreemap = visible;
            _showMap.IsChecked = visible;
            _mapRow.Height = visible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            _mapRow.MinHeight = visible ? 80 : 0;
            _splitRow.Height = new GridLength(visible ? 8 : 0);
            if (visible && Scan != null && !_live) _treemap.Show(ViewRoot);
        }

        // ------------------------------------------------------------------ search

        public async void RunSearch() => await RunSearchAsync();

        public async Task RunSearchAsync()
        {
            if (Scan == null) return;
            _searchCancel?.Cancel();
            var cancel = _searchCancel = new CancellationTokenSource();
            var change = ChangeChoices[Math.Max(0, _changed.SelectedIndex)];
            var query = new SearchQuery
            {
                Text = _query.Text,
                MinSize = SizeChoices[Math.Max(0, _minSize.SelectedIndex)].Bytes,
                ChangedBefore = change.Older ? DateTime.Now.AddDays(-change.Days) : (DateTime?)null,
                ChangedAfter = change.Days > 0 && !change.Older ? DateTime.Now.AddDays(-change.Days) : (DateTime?)null,
                Kind = (FindKind)Math.Max(0, _kind.SelectedIndex),
                OnDisk = RowBase.OnDisk
            };
            _searchStatus.Text = "Searching…";
            var root = ViewRoot;
            try
            {
                var total = 0;
                var found = await Task.Run(() => Queries.Search(root, query, out total, cancel.Token));
                if (cancel.IsCancellationRequested) return;
                _searchRows.Replace(found.Select(n => new ItemRow { Node = n }));
                _searchStatus.Text = total == 0 ? "Nothing found." : total > found.Count
                    ? $"{total:N0} found; showing the biggest {found.Count:N0} ({Format.Size(found.Sum(n => n.Measure(RowBase.OnDisk)))})."
                    : $"{total:N0} found, {Format.Size(found.Sum(n => n.Measure(RowBase.OnDisk)))} in all.";
            }
            catch (OperationCanceledException) { }
        }

        /// <summary>For the self-test: search without typing.</summary>
        public Task SearchFor(string text, int minSizeIndex = 0)
        {
            ShowTab("Search");
            _query.Text = text;
            _minSize.SelectedIndex = minSizeIndex;
            return RunSearchAsync();
        }

        // ------------------------------------------------------------------ selection and actions

        public void Select(Node node, object source)
        {
            if (node == null) return;
            Selected = node;
            _treemap.Select(node);
            if (source == _treemap) _tree.Reveal(node);
            _owner.ShowSelection(node);
        }

        public void ShowMenu(Node node, FrameworkElement at)
        {
            var menu = new ContextMenu { PlacementTarget = at, Placement = PlacementMode.MousePoint };
            void Item(string text, Action action, bool enabled = true, string gesture = null)
            {
                var item = new MenuItem { Header = text, IsEnabled = enabled, InputGestureText = gesture ?? string.Empty };
                item.Click += (s, e) => action();
                menu.Items.Add(item);
            }
            Item(node.IsDir ? "Open folder" : "Open", () => Actions.Open(node), gesture: "Enter");
            Item("Show in File Explorer", () => Actions.ShowInExplorer(node));
            Item("Copy path", () => Actions.CopyPath(node), gesture: "Ctrl+C");
            if (node.IsDir && !node.Has(NodeFlags.Link))
            {
                menu.Items.Add(new Separator());
                Item("Show only this folder", () => SetViewRoot(node), node != ViewRoot);
                Item("Scan this folder again", () => _owner.RescanFolder(node), !_live);
            }
            menu.Items.Add(new Separator());
            var canDelete = node != Scan.Root && !_live;
            Item("Move to Recycle Bin", () => _owner.DeleteNode(node, false), canDelete, "Delete");
            Item("Delete permanently…", () => _owner.DeleteNode(node, true), canDelete, "Shift+Delete");
            menu.IsOpen = true;
        }

        /// <summary>After a delete or a folder was scanned again: update every view.</summary>
        public void AfterChange()
        {
            _tree.Rebuild();
            _tree.RefreshValues();
            RefreshAll();
            if (Selected != null && Selected.Has(NodeFlags.Deleted)) Select(ViewRoot, null);
        }
    }
}
