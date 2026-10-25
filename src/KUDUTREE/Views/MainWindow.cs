using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using Kudutree.Controls;
using Kudutree.Core;

namespace Kudutree.Views
{
    /// <summary>The main window: the start page (drives and folders), scanning, and the results.</summary>
    public sealed class MainWindow : Window
    {
        private readonly ContentControl _page = new ContentControl { Focusable = false };
        private readonly Grid _footer = new Grid();
        private readonly Button _update = Ui.Link(string.Empty, (s, e) => AppInfo.OpenUrl(AppInfo.ReleasesUrl + "/latest"));
        private readonly ResultsView _results;
        private readonly DispatcherTimer _ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private TextBox _pathBox;
        private CancellationTokenSource _cancel;
        private ScanProgress _progress;
        private Action _onTick;

        public MainWindow()
        {
            Width = 1240;
            Height = 820;
            MinWidth = 960;
            MinHeight = 640;
            Dialogs.Prepare(this, "KUDUTREE");

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = Ui.Columns(Ui.Auto, Ui.Star(), Ui.Auto, Ui.Auto, Ui.Auto, Ui.Auto);
            header.Margin = new Thickness(16, 10, 12, 10);
            var brand = Ui.Row(LogoTile.Create(26), Ui.Text("KUDUTREE", 15, bold: true).Margin(10, 0, 0, 0));
            header.Children.Add(brand.At(0, 0));
            _update.Visibility = Visibility.Collapsed;
            header.Children.Add(_update.Margin(0, 0, 14, 0).At(0, 2));
            header.Children.Add(Ui.Button("Drives and folders", "IconDrive", "GhostButton", (s, e) => { if (!IsScanning) ShowStart(); }).At(0, 3));
            header.Children.Add(Ui.Button("Settings", "IconSettings", "GhostButton", (s, e) => new SettingsWindow(this).ShowDialog()).At(0, 4));
            header.Children.Add(Ui.Button("About", "IconInfo", "GhostButton", (s, e) => About.Show(this)).At(0, 5));
            root.Children.Add(new Border { Child = header, BorderThickness = new Thickness(0, 0, 0, 1) }
                .Res(Border.BackgroundProperty, "SurfaceBg").Res(Border.BorderBrushProperty, "BorderBrush").At(0));
            root.Children.Add(_page.At(1));
            root.Children.Add(new Border { Child = _footer, Padding = new Thickness(16, 10, 16, 10), BorderThickness = new Thickness(0, 1, 0, 0) }
                .Res(Border.BackgroundProperty, "SurfaceBg").Res(Border.BorderBrushProperty, "BorderBrush").At(2));
            Content = root;

            _results = new ResultsView(this);
            _ticker.Tick += (s, e) => _onTick?.Invoke();
            ShowStart();
            Loaded += async (s, e) => await CheckForUpdates();
            Closing += (s, e) => _cancel?.Cancel();
        }

        public ResultsView Results => _results;
        public string PageName { get; private set; }
        public bool IsScanning => _cancel != null;
        public ScanResult Scan => _results.Scan;

        private void SetPage(UIElement page, string name)
        {
            _page.Content = page;
            PageName = name;
        }

        internal void SetFooter(UIElement left, params Button[] buttons)
        {
            _footer.Children.Clear();
            _footer.ColumnDefinitions.Clear();
            _footer.ColumnDefinitions.Add(new ColumnDefinition());
            if (left != null) _footer.Children.Add(left.At(0, 0));
            for (var i = 0; i < buttons.Length; i++)
            {
                _footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                buttons[i].Margin = new Thickness(8, 0, 0, 0);
                _footer.Children.Add(buttons[i].At(0, i + 1));
            }
        }

        private async Task CheckForUpdates()
        {
            if (!Settings.CheckForUpdates || SelfTest.Running) return;
            try
            {
                var info = await UpdateChecker.CheckAsync();
                if (info == null || !info.IsNewer || Settings.SkippedVersion == info.Latest.ToString(3)) return;
                _update.Content = $"KUDUTREE {info.Latest.ToString(3)} is available";
                _update.Visibility = Visibility.Visible;
            }
            catch
            {
                // No internet: try again next time.
            }
        }

        // ================================================================== start page

        public void ShowStart()
        {
            var panel = new StackPanel { Margin = new Thickness(28, 24, 28, 24), MaxWidth = 1100, HorizontalAlignment = HorizontalAlignment.Left };
            panel.Children.Add(Ui.Heading("What's taking up the space?", 22));
            panel.Children.Add(Ui.Subtle("Choose a drive or a folder. KUDUTREE shows how much space every folder and file takes, biggest first.", 13).Margin(0, 6, 0, 18));

            var drives = new WrapPanel();
            foreach (var d in SafeDrives()) drives.Children.Add(DriveCard(d));
            panel.Children.Add(drives);

            panel.Children.Add(Ui.Heading("A folder or network share", 15).Margin(0, 22, 0, 8));
            var row = Ui.Columns(Ui.Auto, Ui.Star(), Ui.Auto);
            row.Children.Add(Ui.Button("Choose a folder…", "IconFolder", "SecondaryButton", (s, e) =>
            {
                var folder = Dialogs.PickFolder(this, "Choose a folder to scan", Settings.LastFolder);
                if (folder != null) StartScan(folder, false);
            }).At(0, 0));
            _pathBox = new TextBox { MinHeight = 34 };
            _pathBox.ToolTip = @"A folder path, or a network share like \\server\share";
            _pathBox.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) ScanTyped(); };
            row.Children.Add(_pathBox.Margin(10, 0, 0, 0).At(0, 1));
            row.Children.Add(Ui.Button("Scan", null, "PrimaryButton", (s, e) => ScanTyped()).Margin(8, 0, 0, 0).At(0, 2));
            row.MaxWidth = 760;
            row.HorizontalAlignment = HorizontalAlignment.Left;
            panel.Children.Add(row);
            panel.Children.Add(Ui.Subtle(@"Or type a path, like D:\Projects or \\nas\share. You can also right-click a folder in File Explorer → Show sizes with KUDUTREE (turn it on in Settings).", 12).Margin(0, 8, 0, 0));
            var saved = SavedScan.List().Take(5).ToList();
            panel.Children.Add(Ui.Heading("Saved scans", 15).Margin(0, 22, 0, 8));
            panel.Children.Add(Ui.Button("Open a saved scan…", "IconFolder", "SecondaryButton", (s, e) =>
            {
                var file = Dialogs.PickFile(this, "Open a saved scan", SavedScan.FileFilter);
                if (file != null) OpenSavedScan(file);
            }).Margin(0, 0, 0, 6));
            ((FrameworkElement)panel.Children[panel.Children.Count - 1]).HorizontalAlignment = HorizontalAlignment.Left;
            foreach (var info in saved)
                panel.Children.Add(Ui.Link($"{info.Path} · {info.Started:g} · {Format.Size(info.Measure(Settings.SizeOnDisk))}", (s, e) => OpenSavedScan(info.File)).Margin(0, 4, 0, 0));
            if (saved.Count == 0)
                panel.Children.Add(Ui.Subtle("Save a scan from its results, to look at it again later or see what changed since.", 12));

            var last = Settings.LastFolder;
            if (!string.IsNullOrEmpty(last))
                panel.Children.Add(Ui.Link("Scan again: " + last, (s, e) => StartScan(last, null)).Margin(0, 12, 0, 0));

            SetPage(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false }, "start");
            SetFooter(Ui.Subtle(Admin.IsAdmin ? "Running as administrator: whole NTFS drives use the fast scan." : "The fast scan reads a whole NTFS drive's file index in seconds; it asks for administrator rights.", 12.5, false));
        }

        private void ScanTyped()
        {
            var path = _pathBox.Text.Trim();
            if (path.Length > 0) StartScan(path, null);
        }

        private static List<DriveInfo> SafeDrives()
        {
            try { return DriveInfo.GetDrives().Where(d => d.DriveType != DriveType.CDRom && IsReady(d)).ToList(); }
            catch { return new List<DriveInfo>(); }
        }

        private static bool IsReady(DriveInfo d)
        {
            try { return d.IsReady; }
            catch { return false; }
        }

        private UIElement DriveCard(DriveInfo d)
        {
            string label = string.Empty, format = string.Empty;
            long total = 0, free = 0;
            try
            {
                label = d.VolumeLabel;
                format = d.DriveFormat;
                total = d.TotalSize;
                free = d.TotalFreeSpace;
            }
            catch { /* show what we have */ }
            var letter = d.Name.TrimEnd('\\');
            var kind = d.DriveType == DriveType.Removable ? "USB drive" : d.DriveType == DriveType.Network ? "Network drive" : "Local disk";
            var icon = d.DriveType == DriveType.Removable ? "IconUsb" : d.DriveType == DriveType.Network ? "IconServer" : "IconDrive";
            var used = total - free;

            var title = Ui.Row(Ui.Icon(icon, 18), Ui.Text((string.IsNullOrEmpty(label) ? kind : label) + $" ({letter})", 14, bold: true).Margin(8, 0, 0, 0));
            var bar = new Grid { Height = 8, Margin = new Thickness(0, 12, 0, 6) };
            bar.Children.Add(new Border { CornerRadius = new CornerRadius(4) }.Res(Border.BackgroundProperty, "NeutralChipBg"));
            var fill = new Border { CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left, Width = total > 0 ? 268.0 * used / total : 0 };
            fill.Res(Border.BackgroundProperty, total > 0 && free < total / 10 ? "BadText" : "Accent");
            bar.Children.Add(fill);
            var body = Ui.Column(title, Ui.Subtle($"{kind} · {format}", 12, false).Margin(26, 2, 0, 0), bar,
                Ui.Subtle(total > 0 ? $"{Format.Size(free)} free of {Format.Size(total)}" : "Size unknown", 12, false));

            var buttons = Ui.Row();
            var ntfs = string.Equals(format, "NTFS", StringComparison.OrdinalIgnoreCase) && d.DriveType != DriveType.Network;
            if (ntfs && Admin.IsAdmin)
                buttons.Children.Add(Ui.Small(Ui.Button("Scan", "IconSearch", "PrimaryButton", (s, e) => StartScan(d.Name, true))));
            else if (ntfs)
            {
                var fast = Ui.Small(Ui.Button("Fast scan", "IconShield", "PrimaryButton", (s, e) => StartScan(d.Name, true)));
                fast.ToolTip = "Reads the drive's file index directly: seconds instead of minutes, and every folder is counted. Asks for administrator rights.";
                buttons.Children.Add(fast);
                var normal = Ui.Small(Ui.Button("Scan", null, "SecondaryButton", (s, e) => StartScan(d.Name, false)));
                normal.ToolTip = "Lists folders one by one, without administrator rights. Slower, and folders you can't open aren't counted.";
                buttons.Children.Add(normal.Margin(8, 0, 0, 0));
            }
            else buttons.Children.Add(Ui.Small(Ui.Button("Scan", "IconSearch", "PrimaryButton", (s, e) => StartScan(d.Name, false))));
            body.Children.Add(buttons.Margin(0, 12, 0, 0));
            var card = Ui.Card(body, 16);
            card.Width = 300;
            card.Margin = new Thickness(0, 0, 12, 12);
            return card;
        }

        public bool CanFastScan(string path)
        {
            if (!Paths.IsDriveRoot(path) || !Paths.IsWindows) return false;
            try
            {
                var d = new DriveInfo(path);
                return d.IsReady && d.DriveType != DriveType.Network && string.Equals(d.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        // ================================================================== scanning

        private string _lastPath;
        private bool? _lastFast;

        public void Rescan()
        {
            if (_lastPath != null && !IsScanning) StartScan(_lastPath, _lastFast == true ? true : (bool?)null);
        }

        /// <summary>
        /// Scans a drive or folder. <paramref name="fast"/>: true = the file index scan (restarting as administrator if
        /// needed), false = folder listing, null = the fast scan when it's possible without asking.
        /// </summary>
        public async void StartScan(string path, bool? fast) => await StartScanAsync(path, fast);

        public async Task StartScanAsync(string path, bool? fast)
        {
            if (IsScanning) return;
            path = Paths.Normalize(path);
            if (path.Length == 0) return;
            var canFast = CanFastScan(path);
            var useFast = canFast && (fast == true || (fast == null && Admin.IsAdmin));
            if (useFast && !Admin.IsAdmin)
            {
                if (Admin.RestartElevated(new[] { "--scan", path, "--fast" })) Close();
                return;
            }
            if (!Paths.IsUnc(path) && !Directory.Exists(Paths.Long(path)))
            {
                Dialogs.Error(this, $"{path} wasn't found.");
                return;
            }
            _lastPath = path;
            _lastFast = useFast;
            if (!Paths.IsDriveRoot(path)) Settings.LastFolder = path;

            _cancel = new CancellationTokenSource();
            _progress = new ScanProgress();
            var token = _cancel.Token;
            try
            {
                ScanResult result = null;
                if (useFast)
                {
                    ShowFastProgress(path);
                    try
                    {
                        result = await Task.Run(() =>
                        {
                            using (var device = VolumeDevice.Open(path))
                                return MftScanner.Scan(device, path, _progress, token);
                        }, token);
                    }
                    catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException)
                    {
                        if (token.IsCancellationRequested) throw new OperationCanceledException();
                        if (!SelfTest.Running) Dialogs.Info(this, "The fast scan couldn't read this drive (" + ex.Message.TrimEnd('.') + "). Scanning it folder by folder instead.");
                        else SelfTest.Errors.Add("Fast scan failed: " + ex.Message);
                        _progress = new ScanProgress();
                        useFast = false;
                    }
                }
                if (!useFast)
                {
                    var live = false;
                    result = await Task.Run(() => FolderScanner.Scan(path, _progress, token, started => Dispatcher.Invoke(() =>
                    {
                        // Show the tree straight away and let it grow.
                        AddDriveTotals(started, path);
                        _results.Show(started, true);
                        SetPage(_results, "results");
                        live = true;
                        StartLiveFooter();
                    })), token);
                    if (!live) _results.Show(result, true);
                }
                AddDriveTotals(result, path);
                _ticker.Stop();
                _onTick = null;
                if (useFast) _results.Show(result, false);
                _results.Finished();
                SetPage(_results, "results");
                ShowSelection(_results.Selected ?? result.Root);
            }
            catch (OperationCanceledException)
            {
                _ticker.Stop();
                _onTick = null;
                _cancel = null;
                ShowStart();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _ticker.Stop();
                _onTick = null;
                _cancel = null;
                Dialogs.Error(this, "The scan couldn't finish: " + ex.Message);
                ShowStart();
            }
            finally
            {
                _cancel = null;
            }
        }

        private static void AddDriveTotals(ScanResult result, string path)
        {
            if (!Paths.IsDriveRoot(path) || result.DriveTotal > 0) return;
            try
            {
                var d = new DriveInfo(path);
                result.DriveTotal = d.TotalSize;
                result.DriveFree = d.TotalFreeSpace;
            }
            catch { /* not shown */ }
        }

        public void Stop() => _cancel?.Cancel();

        // ================================================================== saved scans

        public void SaveScan()
        {
            var scan = _results.Scan;
            if (scan == null || IsScanning) return;
            System.IO.Directory.CreateDirectory(SavedScan.Folder);
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save the scan",
                FileName = SavedScan.DefaultName(scan),
                Filter = SavedScan.FileFilter,
                InitialDirectory = SavedScan.Folder,
                OverwritePrompt = true
            };
            if (dialog.ShowDialog(this) != true) return;
            SaveScanTo(dialog.FileName);
        }

        public async void SaveScanTo(string file) => await SaveScanToAsync(file);

        public async Task SaveScanToAsync(string file)
        {
            var scan = _results.Scan;
            Cursor = System.Windows.Input.Cursors.Wait;
            try
            {
                await Task.Run(() => SavedScan.Save(scan, file));
                scan.SavedAs = file;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Dialogs.Error(this, "The scan couldn't be saved: " + ex.Message);
            }
            finally
            {
                Cursor = null;
            }
        }

        /// <summary>Opens a saved scan to look through, without scanning again.</summary>
        public async void OpenSavedScan(string file) => await OpenSavedScanAsync(file);

        public async Task OpenSavedScanAsync(string file)
        {
            if (IsScanning) return;
            try
            {
                Cursor = System.Windows.Input.Cursors.Wait;
                var scan = await Task.Run(() => SavedScan.Load(file));
                scan.SavedAs = file;
                _lastPath = scan.Path;
                _lastFast = scan.Fast;
                _results.Show(scan, false);
                _results.Finished();
                SetPage(_results, "results");
                ShowSelection(scan.Root);
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException)
            {
                Dialogs.Error(this, ex.Message);
            }
            finally
            {
                Cursor = null;
            }
        }

        private void ShowFastProgress(string path)
        {
            var bar = new ProgressBar { Minimum = 0, Maximum = 1000, Height = 8, Margin = new Thickness(0, 16, 0, 10) };
            var phase = Ui.Text("Opening the drive…", 13);
            var numbers = Ui.Subtle(string.Empty, 12.5, false);
            var content = Ui.Column(Ui.Subtle("Fast scan", 12.5, false), Ui.Heading(path, 20).Margin(0, 4, 0, 0), bar, phase, numbers.Margin(0, 4, 0, 0));
            var card = Ui.Card(content, 28);
            card.MaxWidth = 620;
            card.VerticalAlignment = VerticalAlignment.Center;
            SetPage(new Grid { Margin = new Thickness(24), Children = { card } }, "fast");
            SetFooter(Ui.Subtle("Reading the drive's file index. Nothing on the drive is changed.", 12.5, false), Ui.Button("Stop", "IconStop", "SecondaryButton", (s, e) => Stop()));
            _onTick = () =>
            {
                var p = _progress;
                if (p == null) return;
                bar.Value = p.Total > 0 ? 1000.0 * p.Done / p.Total : 0;
                phase.Text = p.Phase + "…";
                numbers.Text = p.Total > 0 ? $"{p.Done:N0} of {p.Total:N0} index records" : string.Empty;
            };
            _ticker.Interval = TimeSpan.FromMilliseconds(150);
            _ticker.Start();
        }

        private void StartLiveFooter()
        {
            var text = Ui.Subtle(string.Empty, 12.5, false);
            var current = Ui.Subtle(string.Empty, 11.5, false);
            SetFooter(Ui.Column(text, current), Ui.Button("Stop", "IconStop", "SecondaryButton", (s, e) => Stop()));
            _onTick = () =>
            {
                var p = _progress;
                text.Text = $"Scanning… {p.Files:N0} files in {p.Folders:N0} folders · {Format.Size(p.Bytes)} so far. You can look around while it runs.";
                current.Text = p.Current;
                _results.LiveTick();
            };
            _ticker.Interval = TimeSpan.FromSeconds(1);
            _ticker.Start();
        }

        // ================================================================== selection and actions

        public void ShowSelection(Node node)
        {
            if (IsScanning && _onTick != null) return; // the footer shows progress while scanning
            if (node == null)
            {
                SetFooter(null);
                return;
            }
            var size = node.Measure(Settings.SizeOnDisk);
            var details = Format.Size(size) + (node.IsDir ? $" · {Format.Count(node.Files, "file")} · {Format.Count(node.Folders, "folder")}" : string.Empty);
            if (node.Mtime > 0) details += " · " + (node.IsDir ? "newest file " : "modified ") + node.Modified.ToString("g");
            var info = Ui.Column(Ui.Text(node.Path, 12.5, bold: true), Ui.Subtle(details, 12, false));
            var open = Ui.Small(Ui.Button(node.IsDir ? "Open" : "Open", "IconExternal", "SecondaryButton", (s, e) => Actions.Open(node)));
            var show = Ui.Small(Ui.Button("Show in Explorer", "IconFolder", "SecondaryButton", (s, e) => Actions.ShowInExplorer(node)));
            var delete = Ui.Small(Ui.Button("Delete…", "IconTrash", "SecondaryButton", (s, e) => DeleteNode(node, false)));
            var protectedWhy = Protection.Reason(node);
            delete.IsEnabled = protectedWhy == null;
            delete.ToolTip = protectedWhy ?? "Move to the Recycle Bin (Shift+Delete in the list deletes permanently)";
            System.Windows.Controls.ToolTipService.SetShowOnDisabled(delete, true);
            SetFooter(info, open, show, delete);
        }

        public async void DeleteNode(Node node, bool permanent) => await DeleteNodeAsync(node, permanent, true);

        public async Task<string> DeleteNodeAsync(Node node, bool permanent, bool ask)
        {
            if (node == null || node.Parent == null || IsScanning) return "Can't delete that.";
            if (Protection.Reason(node) is string why)
            {
                if (ask) Dialogs.Info(this, why);
                return why;
            }
            var what = $"\"{node.Name}\" ({Format.Size(node.Measure(Settings.SizeOnDisk))}{(node.IsDir ? ", " + Format.Count(node.Files, "file") : string.Empty)})";
            if (!permanent && Paths.IsUnc(node.Path))
            {
                if (ask && !Dialogs.Confirm(this, $"Network folders have no Recycle Bin. Delete {what} permanently?\n\nThis can't be undone.")) return "Cancelled.";
                permanent = true;
            }
            else if (ask && permanent && !Dialogs.Confirm(this, $"Delete {what} permanently?\n\nIt won't go to the Recycle Bin, so this can't be undone.")) return "Cancelled.";
            else if (ask && !permanent && Settings.ConfirmRecycle && !Dialogs.Confirm(this, $"Move {what} to the Recycle Bin?")) return "Cancelled.";

            Cursor = System.Windows.Input.Cursors.Wait;
            var error = await Actions.Delete(node, permanent);
            Cursor = null;
            if (!Actions.Exists(node)) Queries.Remove(_results.Scan, node);
            else if (node.IsDir) await RescanFolderAsync(node); // partly deleted: count what's left
            _results.AfterChange();
            ShowSelection(_results.Selected);
            if (error != null && error != "Cancelled." && ask) Dialogs.Error(this, "Not everything could be deleted: " + error);
            return error;
        }

        public async void RescanFolder(Node node) => await RescanFolderAsync(node);

        public async Task RescanFolderAsync(Node node)
        {
            try
            {
                Cursor = System.Windows.Input.Cursors.Wait;
                var fresh = await Task.Run(() => FolderScanner.Scan(node.Path));
                Queries.Replace(_results.Scan, node, fresh.Root);
                _results.AfterChange();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Dialogs.Error(this, "That folder couldn't be scanned: " + ex.Message);
            }
            finally
            {
                Cursor = null;
            }
        }
    }
}
