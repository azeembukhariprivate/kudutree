using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Kudutree.Controls;
using Kudutree.Core;

namespace Kudutree.Views
{
    /// <summary>The "Old & empty", "Duplicates" and "Changes" tabs.</summary>
    public sealed partial class ResultsView
    {
        private static readonly (string Text, int Days)[] AgeChoices = { ("for a year", 365), ("for 2 years", 730), ("for 3 years", 1095), ("for 5 years", 1826) };
        private static readonly (string Text, long Bytes)[] OldSizeChoices = { ("of any size", 1), ("of 1 MB or more", 1L << 20), ("of 10 MB or more", 10L << 20), ("of 100 MB or more", 100L << 20) };
        private static readonly (string Text, long Bytes)[] DupSizeChoices = { ("100 KB or more", 100L << 10), ("1 MB or more", 1L << 20), ("10 MB or more", 10L << 20), ("100 MB or more", 100L << 20), ("Any size", 1) };

        private readonly RowList<ItemRow> _oldRows = new RowList<ItemRow>();
        private readonly RowList<ItemRow> _emptyRows = new RowList<ItemRow>();
        private readonly RowList<ItemRow> _dupRows = new RowList<ItemRow>();
        private readonly RowList<ItemRow> _changeRows = new RowList<ItemRow>();
        private readonly ComboBox _age = new ComboBox { MinWidth = 120 };
        private readonly ComboBox _oldSize = new ComboBox { MinWidth = 150 };
        private readonly ComboBox _dupSize = new ComboBox { MinWidth = 150 };
        private readonly ComboBox _savedScans = new ComboBox { MinWidth = 320 };
        private readonly TextBlock _oldStatus = Ui.Subtle(string.Empty, 12, false);
        private readonly TextBlock _emptyStatus = Ui.Subtle(string.Empty, 12, false);
        private readonly TextBlock _dupStatus = Ui.Subtle(string.Empty, 12, false);
        private readonly TextBlock _changeStatus = Ui.Subtle(string.Empty, 12);
        private UIElement _oldPage, _dupPage, _changePage;
        private CancellationTokenSource _dupCancel;
        private ComparisonResult _comparison;
        private string _changeFilter = "All";

        public IReadOnlyList<ItemRow> OldRows => _oldRows;
        public IReadOnlyList<ItemRow> EmptyRows => _emptyRows;
        public IReadOnlyList<ItemRow> DuplicateRows => _dupRows;
        public IReadOnlyList<ItemRow> ChangeRows => _changeRows;
        public ComparisonResult LastComparison => _comparison;

        private Button RecycleEmptyButton => _recycleEmptyButton ?? (_recycleEmptyButton = Ui.Small(Ui.Button("Move them all to the Recycle Bin", "IconTrash", "SecondaryButton", async (s, e) => await RecycleEmptyFolders(true))));
        private Button _recycleEmptyButton;

        // ================================================================== old and empty

        private UIElement OldPage()
        {
            if (_oldPage == null)
            {
                foreach (var c in AgeChoices) _age.Items.Add(c.Text);
                foreach (var c in OldSizeChoices) _oldSize.Items.Add(c.Text);
                _age.SelectedIndex = 0;
                _oldSize.SelectedIndex = 1;
                _age.SelectionChanged += (s, e) => FillOld();
                _oldSize.SelectionChanged += (s, e) => FillOld();

                var oldHead = Ui.Row(Ui.Text("Files not opened or changed ", 13), _age, Ui.Text("  ", 13), _oldSize);
                var grid = new Grid();
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
                grid.Children.Add(oldHead.Margin(4, 0, 0, 4).At(0));
                grid.Children.Add(Ui.Column(_oldStatus, Ui.Subtle("Windows doesn't always record when a file is opened, so a file counts as old only if it wasn't changed or (as far as Windows knows) opened.", 11.5))
                    .Margin(4, 0, 0, 6).At(1));
                grid.Children.Add(ItemList(_oldRows).At(2));
                var emptyHead = Ui.Columns(Ui.Star(), Ui.Auto);
                emptyHead.Children.Add(Ui.Column(Ui.Heading("Empty folders", 14), _emptyStatus).At(0, 0));
                emptyHead.Children.Add(RecycleEmptyButton.At(0, 1));
                grid.Children.Add(emptyHead.Margin(4, 14, 0, 6).At(3));
                grid.Children.Add(ItemList(_emptyRows).At(4));
                _oldPage = grid;
            }
            if (_oldRows.Count == 0 && _emptyRows.Count == 0) FillOld();
            return _oldPage;
        }

        private void FillOld()
        {
            if (Scan == null || _live || _age.SelectedIndex < 0) return;
            var days = AgeChoices[_age.SelectedIndex].Days;
            var min = OldSizeChoices[Math.Max(0, _oldSize.SelectedIndex)].Bytes;
            var old = Queries.OldFiles(ViewRoot, DateTime.Now.AddDays(-days), min, RowBase.OnDisk, out var total, out var totalSize, 2000);
            _oldRows.Replace(old.Select(n => new ItemRow { Node = n, ShowLastUsed = true }));
            _oldStatus.Text = total == 0 ? "None found." : $"{Format.Count(total, "file")}, {Format.Size(totalSize)} in all" + (total > old.Count ? $" (the biggest {old.Count:N0} are listed)" : string.Empty) + ". The date is when each was last opened or changed.";

            var empty = Queries.EmptyFolders(ViewRoot);
            _emptyRows.Replace(empty.Select(n => new ItemRow { Node = n, Extra = n.Folders > 0 ? Format.Count(n.Folders, "empty folder") + " inside" : string.Empty }));
            _emptyStatus.Text = empty.Count == 0 ? "No empty folders." : $"{Format.Count(empty.Count, "folder")} with no files in them (counting empty folders inside only once).";
            RecycleEmptyButton.IsEnabled = empty.Count > 0;
        }

        /// <summary>Moves every listed empty folder to the Recycle Bin. Returns how many went.</summary>
        public async Task<int> RecycleEmptyFolders(bool ask)
        {
            var folders = _emptyRows.Select(r => r.Node).Where(n => n != null && !n.Has(NodeFlags.Deleted)).ToList();
            if (folders.Count == 0) return 0;
            if (ask && !Dialogs.Confirm(Window.GetWindow(this), $"Move {Format.Count(folders.Count, "empty folder")} to the Recycle Bin?")) return 0;
            var done = 0;
            foreach (var folder in folders)
            {
                if (folder.Files > 0 || Protection.Reason(folder) != null) continue; // something was added since, or Windows needs it
                await Actions.Delete(folder, false);
                if (Actions.Exists(folder)) continue;
                Queries.Remove(Scan, folder);
                done++;
            }
            AfterChange();
            return done;
        }

        // ================================================================== duplicates

        private UIElement DuplicatesPage()
        {
            if (_dupPage != null) return _dupPage;
            foreach (var c in DupSizeChoices) _dupSize.Items.Add(c.Text);
            _dupSize.SelectedIndex = 1;
            var find = Ui.Button("Find duplicates", "IconSearch", "PrimaryButton", async (s, e) => await FindDuplicates());
            var stop = Ui.Button("Stop", "IconStop", "SecondaryButton", (s, e) => _dupCancel?.Cancel());
            stop.Visibility = Visibility.Collapsed;
            _findDupsButton = find;
            _stopDupsButton = stop;
            var head = Ui.Row(Ui.Text("Files of ", 13), _dupSize, find.Margin(10, 0, 0, 0), stop.Margin(8, 0, 0, 0));
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition());
            grid.Children.Add(head.Margin(4, 0, 0, 6).At(0));
            grid.Children.Add(Ui.Column(_dupStatus,
                    Ui.Subtle("Files count as duplicates only when their whole contents are the same. Online-only OneDrive files aren't read (that would download them), and hard links are one file already.", 11.5))
                .Margin(4, 0, 0, 8).At(1));
            grid.Children.Add(ItemList(_dupRows).At(2));
            _dupStatus.Text = "Looks for identical files in " + (ViewRoot?.Path ?? "the scan") + ".";
            return _dupPage = grid;
        }

        private Button _findDupsButton, _stopDupsButton;

        public async Task FindDuplicates(long? minSize = null)
        {
            if (Scan == null || _live || _dupCancel != null) return;
            DuplicatesPage();
            var min = minSize ?? DupSizeChoices[Math.Max(0, _dupSize.SelectedIndex)].Bytes;
            var cancel = _dupCancel = new CancellationTokenSource();
            var progress = new DuplicateProgress();
            _findDupsButton.IsEnabled = false;
            _stopDupsButton.Visibility = Visibility.Visible;
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            timer.Tick += (s, e) => _dupStatus.Text = $"{progress.Phase}… {progress.Done:N0} of {progress.Total:N0} files · {Format.Size(progress.BytesRead)} read";
            timer.Start();
            var root = ViewRoot;
            try
            {
                var groups = await Task.Run(() => Duplicates.Find(root, min, progress, cancel.Token));
                var rows = new List<ItemRow>();
                foreach (var g in groups.Take(2000))
                {
                    rows.Add(new ItemRow
                    {
                        Header = true,
                        IconKey = "IconCopy",
                        Title = $"{g.Files.Count} copies of {g.Files[0].Name}",
                        Subtitle = $"{Format.Size(g.Size)} each",
                        Value = Format.Size(g.Wasted),
                        Extra = "could be freed",
                        Date = string.Empty
                    });
                    rows.AddRange(g.Files.Select(f => new ItemRow { Node = f, Nested = true, Value = Format.Size(f.Size) }));
                }
                _dupRows.Replace(rows);
                var wasted = groups.Sum(g => g.Wasted);
                _dupStatus.Text = groups.Count == 0 ? "No duplicates found." :
                    $"{Format.Count(groups.Count, "set")} of identical files; {Format.Size(wasted)} could be freed by keeping one of each." +
                    (groups.Count > 2000 ? " The 2,000 biggest sets are listed." : string.Empty) +
                    (progress.Unreadable > 0 ? $" {Format.Count(progress.Unreadable, "file")} couldn't be read." : string.Empty);
            }
            catch (OperationCanceledException)
            {
                _dupStatus.Text = "Stopped.";
            }
            finally
            {
                timer.Stop();
                _dupCancel = null;
                _findDupsButton.IsEnabled = true;
                _stopDupsButton.Visibility = Visibility.Collapsed;
            }
        }

        // ================================================================== changes since a saved scan

        private UIElement ChangesPage()
        {
            if (_changePage == null)
            {
                var compare = Ui.Button("Compare", null, "PrimaryButton", async (s, e) => await CompareWithSelected());
                var other = Ui.Button("Other file…", "IconFolder", "SecondaryButton", async (s, e) =>
                {
                    var file = Dialogs.PickFile(Window.GetWindow(this), "Choose a saved scan to compare with", SavedScan.FileFilter);
                    if (file != null) await CompareWith(file);
                });
                var head = Ui.Row(Ui.Text("Compare with ", 13), _savedScans, compare.Margin(10, 0, 0, 0), other.Margin(8, 0, 0, 0));
                var filters = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
                foreach (var f in new[] { "All", "Folders", "Files", "New", "Removed" })
                {
                    var chip = new RadioButton { Content = f, GroupName = "changefilter", IsChecked = f == "All" }.Styled("ChipRadio");
                    chip.Checked += (s, e) =>
                    {
                        _changeFilter = f;
                        FillChanges();
                    };
                    filters.Children.Add(chip);
                }
                var grid = new Grid();
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition());
                grid.Children.Add(Ui.Column(head, filters).Margin(4, 0, 0, 6).At(0));
                grid.Children.Add(_changeStatus.Margin(4, 0, 0, 8).At(1));
                grid.Children.Add(ItemList(_changeRows).At(2));
                _changePage = grid;
            }
            FillSavedScans();
            return _changePage;
        }

        private void FillSavedScans()
        {
            _savedScans.Items.Clear();
            if (Scan == null) return;
            foreach (var info in SavedScan.List(Scan.Path).Where(i => i.Started != Scan.Started))
                _savedScans.Items.Add(new ComboBoxItem { Content = $"{info.Started:g} · {Format.Size(info.Measure(RowBase.OnDisk))} · {Format.Count(info.Files, "file")}", Tag = info.File });
            if (_savedScans.Items.Count > 0) _savedScans.SelectedIndex = 0;
            if (_comparison == null)
                _changeStatus.Text = _savedScans.Items.Count == 0
                    ? "No saved scans of this place yet. Use \"Save scan\" now, and next time you scan it you'll see here what changed."
                    : "Choose a saved scan to see what grew, shrank, appeared or went away since then.";
        }

        private async Task CompareWithSelected()
        {
            if ((_savedScans.SelectedItem as ComboBoxItem)?.Tag is string file) await CompareWith(file);
        }

        public async Task CompareWith(string file)
        {
            if (Scan == null || _live) return;
            ChangesPage();
            _changeStatus.Text = "Comparing…";
            var now = Scan;
            var onDisk = RowBase.OnDisk;
            try
            {
                _comparison = await Task.Run(() =>
                {
                    var then = SavedScan.Load(file);
                    return Comparison.Compare(now, then, onDisk);
                });
                FillChanges();
            }
            catch (System.IO.InvalidDataException ex)
            {
                _changeStatus.Text = ex.Message;
            }
            catch (System.IO.IOException ex)
            {
                _changeStatus.Text = "The saved scan couldn't be opened: " + ex.Message;
            }
        }

        private void FillChanges()
        {
            if (_comparison == null) return;
            var c = _comparison;
            var under = ViewRoot?.Path ?? Scan.Path;
            IEnumerable<Change> list = c.Changes.Where(x => x.Path.StartsWith(under, StringComparison.OrdinalIgnoreCase) && (x.Now == null || !x.Now.Has(NodeFlags.Deleted)));
            switch (_changeFilter)
            {
                case "Folders": list = list.Where(x => x.IsDir); break;
                case "Files": list = list.Where(x => !x.IsDir); break;
                case "New": list = list.Where(x => x.Kind == ChangeKind.New); break;
                case "Removed": list = list.Where(x => x.Kind == ChangeKind.Removed); break;
            }
            var shown = list.Take(3000).ToList();
            _changeRows.Replace(shown.Select(x => new ItemRow
            {
                Node = x.Now,
                Title = x.Path.Length > under.Length ? x.Path.Substring(under.Length).TrimStart('\\', '/') : x.Path,
                Subtitle = x.Kind == ChangeKind.New ? "New · " + Format.Size(x.After)
                    : x.Kind == ChangeKind.Removed ? "Gone · was " + Format.Size(x.Before)
                    : $"Was {Format.Size(x.Before)}, now {Format.Size(x.After)}",
                Value = (x.Delta >= 0 ? "+" : "−") + Format.Size(Math.Abs(x.Delta)),
                Extra = x.Kind == ChangeKind.New ? "New" : x.Kind == ChangeKind.Removed ? "Removed" : x.Kind == ChangeKind.Grew ? "Grew" : "Shrank",
                IconKey = x.IsDir ? "IconFolder" : x.Now == null ? "IconFile" : null,
                Date = string.Empty
            }));
            var since = c.Then.Started.ToString("g");
            _changeStatus.Text = $"Since {since}: {(c.Delta >= 0 ? "+" : "−")}{Format.Size(Math.Abs(c.Delta))} (was {Format.Size(c.Before)}, now {Format.Size(c.After)}). " +
                                 (c.Changes.Count == 0 ? "Nothing changed." : $"{Format.Count(c.Changes.Count, "item")} changed, biggest change first" + (shown.Count >= 3000 ? "; the first 3,000 are listed." : "."));
        }

        // ================================================================== keeping the tabs in step

        private void ResetExtras()
        {
            _oldRows.Replace(Enumerable.Empty<ItemRow>());
            _emptyRows.Replace(Enumerable.Empty<ItemRow>());
            _dupRows.Replace(Enumerable.Empty<ItemRow>());
            _changeRows.Replace(Enumerable.Empty<ItemRow>());
            _dupCancel?.Cancel();
            _comparison = null;
            _dupStatus.Text = string.Empty;
            _changeStatus.Text = string.Empty;
        }

        /// <summary>After deleting: drop deleted items from the lists (and duplicate sets with one file left).</summary>
        private void PruneExtras()
        {
            foreach (var list in new[] { _oldRows, _emptyRows, _changeRows })
                foreach (var r in list.Where(r => r.Node != null && r.Node.Has(NodeFlags.Deleted)).ToList()) list.Remove(r);
            var rows = _dupRows.ToList();
            var kept = new List<ItemRow>();
            for (var i = 0; i < rows.Count; i++)
            {
                if (!rows[i].Header) continue;
                var files = new List<ItemRow>();
                for (var j = i + 1; j < rows.Count && !rows[j].Header; j++)
                    if (!rows[j].Node.Has(NodeFlags.Deleted)) files.Add(rows[j]);
                if (files.Count < 2) continue;
                kept.Add(rows[i]);
                kept.AddRange(files);
            }
            if (kept.Count != rows.Count) _dupRows.Replace(kept);
        }
    }
}
