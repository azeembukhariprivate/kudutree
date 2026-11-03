using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Kudutree.Controls;
using Kudutree.Core;

namespace Kudutree.Views
{
    /// <summary>
    /// "Safe to clean": kinds of files that can go without harm, measured, with a tick box each. Your own files go to the
    /// Recycle Bin; Windows-wide leftovers (administrator rights) are deleted for good after asking; Windows.old and old
    /// system parts are left to Windows' own cleanup.
    /// </summary>
    public sealed class CleanView : Grid
    {
        private readonly MainWindow _owner;
        private readonly Places _places;
        private readonly List<CleanSpot> _spots;
        private readonly Dictionary<string, Measured> _measured = new Dictionary<string, Measured>();
        private readonly Dictionary<string, CheckBox> _boxes = new Dictionary<string, CheckBox>();
        private readonly Dictionary<string, TextBlock> _sizes = new Dictionary<string, TextBlock>();
        private readonly Border _note;
        private readonly TextBlock _noteText = Ui.Text(string.Empty, 13, wrap: true);
        private readonly StackPanel _noteButtons = Ui.Row();
        private readonly Button _clean;
        private readonly Button _stop;
        private readonly TextBlock _total = Ui.Subtle(string.Empty, 12.5, false);
        private CancellationTokenSource _working;

        /// <summary>For the self-test: don't ask before cleaning.</summary>
        public bool SkipConfirm { get; set; }

        public CleanView(MainWindow owner, Places places = null)
        {
            _owner = owner;
            _places = places ?? Places.ThisPc();
            _spots = Cleanup.Spots(_places);
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var panel = new StackPanel { Margin = new Thickness(28, 24, 28, 24), MaxWidth = 980, HorizontalAlignment = HorizontalAlignment.Left };
            panel.Children.Add(Ui.Heading("Safe to clean", 22));
            panel.Children.Add(Ui.Subtle("Kinds of files that can go without harm, and how much room each gives back. Only these exact folders are cleaned; "
                                         + "the folders themselves stay, and anything in use is left.", 13).Margin(0, 6, 0, 14));
            _note = Ui.Banner("IconInfo", Ui.Column(_noteText, _noteButtons.Margin(0, 8, 0, 0)));
            _note.Visibility = Visibility.Collapsed;
            panel.Children.Add(_note.Margin(0, 0, 0, 14));

            panel.Children.Add(Section("Your files", "Ticked to start with. They go to the Recycle Bin, so you can still get them back."));
            foreach (var s in _spots.Where(x => x.Way == CleanWay.RecycleBin)) panel.Children.Add(Row(s, true));

            var admin = Admin.IsAdmin;
            panel.Children.Add(Section("Windows-wide leftovers", admin
                ? "Not ticked to start with. Windows doesn't put these in the Recycle Bin, so they're deleted for good (you're asked first)."
                : "These need administrator rights, and are deleted for good (you're asked first)."));
            if (!admin)
                panel.Children.Add(Ui.Small(Ui.Button("Restart KUDUTREE as administrator", "IconShield", "SecondaryButton", (s, e) =>
                {
                    if (Admin.RestartElevated(new[] { "--clean" })) _owner.Close();
                })).Margin(0, 0, 0, 8));
            foreach (var s in _spots.Where(x => x.Way == CleanWay.Permanently)) panel.Children.Add(Row(s, false, admin));

            panel.Children.Add(Section("Only Windows' own cleanup can remove these", "They're tied to how Windows runs and updates, so KUDUTREE doesn't delete them file by file."));
            foreach (var s in _spots.Where(x => x.Way == CleanWay.WindowsCleanup)) panel.Children.Add(Row(s, false));
            panel.Children.Add(Ui.Small(Ui.Button("Open Windows' cleanup", "IconExternal", "SecondaryButton", (s, e) => OpenWindowsCleanup())).Margin(0, 8, 0, 0));

            Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });

            _clean = Ui.Button("Clean", "IconTrash", "PrimaryButton", async (s, e) => await Clean());
            _stop = Ui.Button("Stop", "IconStop", "SecondaryButton", (s, e) => _working?.Cancel());
            _stop.Visibility = Visibility.Collapsed;
        }

        /// <summary>The footer for this page (the total ticked and the buttons).</summary>
        public void ShowFooter() =>
            _owner.SetFooter(_total, Ui.Button("Measure again", "IconRefresh", "GhostButton", async (s, e) => await Measure()), _stop, _clean);

        // ================================================================ for the self-test

        public bool IsMeasuring { get; private set; }
        public bool IsCleaning => _working != null;
        public string Note => _note.Visibility == Visibility.Visible ? _noteText.Text : null;
        public Measured MeasuredOf(string id) => _measured.TryGetValue(id, out var m) ? m : null;

        public void Tick(string id, bool on)
        {
            if (_boxes.TryGetValue(id, out var b) && b.IsEnabled) b.IsChecked = on;
        }

        public IEnumerable<string> Ticked => _boxes.Where(b => b.Value.IsChecked == true && b.Value.IsEnabled).Select(b => b.Key);

        // ================================================================ building

        private static UIElement Section(string title, string text) =>
            Ui.Column(Ui.Heading(title, 15), Ui.Subtle(text, 12.5, true).Margin(0, 3, 0, 6)).Margin(0, 14, 0, 0);

        private UIElement Row(CleanSpot spot, bool ticked, bool enabled = true)
        {
            var g = Ui.Columns(Ui.Auto, Ui.Star(), Ui.Auto);
            if (spot.Way != CleanWay.WindowsCleanup)
            {
                var box = new CheckBox { IsChecked = ticked, IsEnabled = enabled, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 10, 0) };
                System.Windows.Automation.AutomationProperties.SetName(box, spot.Name);
                box.Click += (s, e) => UpdateTotal();
                _boxes[spot.Id] = box;
                g.Children.Add(box.At(0, 0));
            }
            else g.Children.Add(Ui.Icon("IconInfo", 15).Margin(2, 2, 12, 0).At(0, 0));
            var text = Ui.Column(Ui.Text(spot.Name, 13.5, bold: true), Ui.Subtle(spot.About, 12, true).Margin(0, 2, 0, 0));
            var folder = spot.Folders.FirstOrDefault(f => !string.IsNullOrEmpty(f) && Directory.Exists(f));
            if (folder != null) text.Children.Add(Ui.Link("Show the files", (s, e) => AppInfo.OpenFolder(folder)).Margin(0, 2, 0, 0));
            g.Children.Add(text.At(0, 1));
            var size = Ui.Text(spot.Folders.Count == 0 ? "–" : "Measuring...", 13, bold: true);
            size.HorizontalAlignment = HorizontalAlignment.Right;
            size.MinWidth = 110;
            size.TextAlignment = TextAlignment.Right;
            _sizes[spot.Id] = size;
            g.Children.Add(size.Margin(16, 0, 0, 0).At(0, 2));
            return new Border { Child = g, Padding = new Thickness(12, 10, 12, 10), BorderThickness = new Thickness(0, 0, 0, 1) }.Res(Border.BorderBrushProperty, "BorderBrush");
        }

        // ================================================================ measuring

        public async Task Measure()
        {
            if (IsMeasuring || IsCleaning) return;
            IsMeasuring = true;
            _clean.IsEnabled = false;
            _measured.Clear();
            foreach (var s in _spots.Where(x => x.Folders.Count > 0)) _sizes[s.Id].Text = "Measuring...";
            foreach (var spot in _spots.Where(x => x.Folders.Count > 0))
            {
                var m = await Task.Run(() => Cleanup.Measure(spot, DateTime.Now));
                _measured[spot.Id] = m;
                _sizes[spot.Id].Text = m.Bytes == 0 ? "Nothing" : Format.Size(m.Bytes);
                _sizes[spot.Id].ToolTip = Format.Count(m.Files, "file") + (m.TooNew > 0 ? $" ({m.TooNew:N0} recent ones left)" : string.Empty);
                UpdateTotal();
            }
            IsMeasuring = false;
            _clean.IsEnabled = true;
            UpdateTotal();
        }

        private void UpdateTotal()
        {
            var bytes = Ticked.Select(id => MeasuredOf(id)?.Bytes ?? 0).Sum();
            _total.Text = IsMeasuring ? "Measuring..." : bytes == 0 ? "Tick what to clean." : $"Ticked: {Format.Size(bytes)}";
            _clean.Content = Ui.Label("IconTrash", bytes == 0 ? "Clean" : "Clean " + Format.Size(bytes));
        }

        // ================================================================ cleaning

        /// <summary>Cleans what's ticked. Returns what was done, or null when nothing was (cancelled before starting).</summary>
        public async Task<CleanResult> Clean()
        {
            if (IsMeasuring || IsCleaning) return null;
            var chosen = Ticked.Select(MeasuredOf).Where(m => m != null && m.Items.Count > 0).ToList();
            if (chosen.Count == 0)
            {
                ShowNote("Nothing ticked to clean.");
                return null;
            }
            var recycled = chosen.Where(m => m.Spot.Way == CleanWay.RecycleBin).Sum(m => m.Bytes);
            var permanent = chosen.Where(m => m.Spot.Way == CleanWay.Permanently).Sum(m => m.Bytes);
            if (!SkipConfirm)
            {
                if (permanent > 0 && !Dialogs.Confirm(_owner, $"Delete {Format.Size(permanent)} of Windows-wide leftovers for good? They don't go to the Recycle Bin."))
                    return null;
                if (permanent == 0 && Settings.ConfirmRecycle && !Dialogs.Confirm(_owner, $"Move {Format.Size(recycled)} of files to the Recycle Bin?"))
                    return null;
            }
            _working = new CancellationTokenSource();
            var token = _working.Token;
            _clean.Visibility = Visibility.Collapsed;
            _stop.Visibility = Visibility.Visible;
            var total = recycled + permanent;
            ShowNote("Cleaning...");
            try
            {
                var last = DateTime.MinValue;
                var result = await Task.Run(() => Cleanup.Clean(chosen, Recycler.Recycle, done =>
                {
                    if ((DateTime.UtcNow - last).TotalMilliseconds < 200) return;
                    last = DateTime.UtcNow;
                    Dispatcher.BeginInvoke(new Action(() => _noteText.Text = $"Cleaning: {Format.Size(done)} of {Format.Size(total)}"));
                }, token));
                Report(result);
                return result;
            }
            catch (OperationCanceledException)
            {
                ShowNote("Stopped. What was cleaned so far is gone; the rest is still there.");
                return null;
            }
            finally
            {
                _working = null;
                _clean.Visibility = Visibility.Visible;
                _stop.Visibility = Visibility.Collapsed;
                await Measure();
            }
        }

        private void Report(CleanResult r)
        {
            var text = $"Cleaned {Format.Size(r.Freed)} ({Format.Count(r.FilesCleaned, "file")}).";
            if (r.Skipped.Count > 0) text += $" {Format.Count(r.Skipped.Count, "item was", "items were")} left because a program has them open (close it and clean again).";
            var bin = Recycler.BinSize();
            if (bin > 0) text += $" {Format.Size(bin)} is in the Recycle Bin: the space comes back when it's emptied.";
            ShowNote(text, bin > 0 ? Ui.Small(Ui.Button("Empty the Recycle Bin", "IconTrash", "SecondaryButton", (s, e) =>
            {
                Recycler.EmptyBin(_owner);
                Report(new CleanResult { Freed = r.Freed, FilesCleaned = r.FilesCleaned });
            })) : null);
        }

        private void ShowNote(string text, Button button = null)
        {
            _noteText.Text = text;
            _noteButtons.Children.Clear();
            if (button != null) _noteButtons.Children.Add(button);
            _noteButtons.Visibility = button != null ? Visibility.Visible : Visibility.Collapsed;
            _note.Visibility = Visibility.Visible;
        }

        private static void OpenWindowsCleanup()
        {
            try { Process.Start(new ProcessStartInfo("cleanmgr.exe") { UseShellExecute = true }); }
            catch (Exception)
            {
                try { Process.Start(new ProcessStartInfo("ms-settings:storagesense") { UseShellExecute = true }); }
                catch (Exception) { /* nothing more to try */ }
            }
        }
    }

    /// <summary>Windows' Recycle Bin: moving many files into it at once, its size, and emptying it.</summary>
    public static class Recycler
    {
        /// <summary>Moves files and folders to the Recycle Bin, without asking or showing anything. True when all of it worked.</summary>
        public static bool Recycle(IList<string> paths)
        {
            if (paths.Count == 0) return true;
            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = string.Join("\0", paths.Select(p => p.StartsWith(@"\\?\") ? p.Substring(4) : p)) + "\0\0",
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_NOCONFIRMMKDIR | FOF_WANTNUKEWARNING
            };
            try
            {
                var code = SHFileOperation(ref op);
                return code == 0 && !op.fAnyOperationsAborted;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>How much is in the Recycle Bin now (all drives).</summary>
        public static long BinSize()
        {
            try
            {
                // The answer is laid out differently by 32- and 64-bit Windows.
                if (IntPtr.Size == 8)
                {
                    var info = new SHQUERYRBINFO64 { cbSize = Marshal.SizeOf(typeof(SHQUERYRBINFO64)) };
                    return SHQueryRecycleBin(null, ref info) == 0 ? info.i64Size : 0;
                }
                var info32 = new SHQUERYRBINFO32 { cbSize = Marshal.SizeOf(typeof(SHQUERYRBINFO32)) };
                return SHQueryRecycleBin(null, ref info32) == 0 ? info32.i64Size : 0;
            }
            catch (Exception) { return 0; }
        }

        /// <summary>Empties the Recycle Bin; Windows asks first.</summary>
        public static void EmptyBin(Window owner)
        {
            var hwnd = owner == null ? IntPtr.Zero : new System.Windows.Interop.WindowInteropHelper(owner).Handle;
            try { SHEmptyRecycleBin(hwnd, null, 0); }
            catch (Exception) { /* Windows shows its own messages */ }
        }

        private const uint FO_DELETE = 3;
        private const ushort FOF_SILENT = 0x0004, FOF_NOCONFIRMATION = 0x0010, FOF_ALLOWUNDO = 0x0040, FOF_NOCONFIRMMKDIR = 0x0200, FOF_NOERRORUI = 0x0400;
        // If the Recycle Bin can't take something (too big, a network drive), Windows asks before deleting it for good,
        // instead of deleting it without a word.
        private const ushort FOF_WANTNUKEWARNING = 0x4000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        private struct SHQUERYRBINFO64
        {
            public int cbSize;
            public long i64Size;
            public long i64NumItems;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct SHQUERYRBINFO32
        {
            public int cbSize;
            public long i64Size;
            public long i64NumItems;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHQueryRecycleBin(string pszRootPath, ref SHQUERYRBINFO64 pSHQueryRBInfo);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHQueryRecycleBin(string pszRootPath, ref SHQUERYRBINFO32 pSHQueryRBInfo);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHEmptyRecycleBin(IntPtr hwnd, string pszRootPath, uint dwFlags);
    }
}
