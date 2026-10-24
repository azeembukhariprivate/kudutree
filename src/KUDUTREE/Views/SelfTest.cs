using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Kudutree.Core;

namespace Kudutree.Views
{
    /// <summary>
    /// KUDUTREE.exe --self-test: creates a small virtual drive (VHD), fills it with known files (compressed, sparse, hard
    /// links, a junction, tiny files), scans it both ways (fast scan of the file index, and folder listing) and checks
    /// every size against what was written and against each other; runs a fast scan of the system drive; then drives
    /// every screen (tree, treemap, file types, largest files, search, delete, scan again) in both themes. Needs
    /// administrator rights (the GitHub build has them). Writes self-test.log next to the exe; exits with 0 when everything passed.
    /// </summary>
    public static class SelfTest
    {
        public static bool Running { get; private set; }
        public static List<string> Errors { get; } = new List<string>();

        private static StringBuilder _log;
        private static string _temp;
        private static string _vhd;
        private static string _drive;
        private static string Data => _drive + @"\Data";

        public static int Run()
        {
            Running = true;
            _log = new StringBuilder();
            _temp = Path.Combine(Path.GetTempPath(), "kudutree-selftest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_temp);

            Step("Running as administrator", () => Expect(Admin.IsAdmin, "administrator rights (needed for the virtual drive and the fast scan)"));
            Step("Create a test drive (VHD, NTFS) with known files", CreateDrive);
            ScanResult walk = null, fast = null;
            Step("Folder listing scan of the test drive", () => walk = WalkScan());
            Step("Fast scan (file index) of the test drive", () => fast = FastScan());
            Step("Both scans agree", () => Compare(walk, fast));
            Step("Fast scan of the system drive", SystemDriveScan);
            Step("Right-click menu", () =>
            {
                ShellIntegration.Register();
                Expect(ShellIntegration.IsRegistered, "the menu is added");
                Expect((ShellIntegration.Command() ?? string.Empty).Contains("--scan \"%1\""), "folder command");
                Expect((ShellIntegration.Command(true) ?? string.Empty).Contains("--scan \"%1\""), "drive command");
                ShellIntegration.Remove();
                Expect(!ShellIntegration.IsRegistered, "the menu is removed");
            });

            Step("Load themes and styles", () => ThemeManager.Init(ThemeMode.Light));
            Step("Theme keys exist", () =>
            {
                foreach (var key in new[] { "WindowBg", "SurfaceBg", "SurfaceAltBg", "Text", "SubtleText", "Accent", "NeutralChipBg", "TableList", "TabRadio", "SegmentRadio",
                                            "PrimaryButton", "SecondaryButton", "GhostButton", "LinkButton", "Card", "IconFolder", "IconFile", "IconLink", "IconMore", "IconDrive",
                                            "IconUsb", "IconServer", "IconShield", "IconSearch", "IconRefresh", "IconUp", "IconTrash", "IconExternal", "IconStop" })
                    Application.Current.FindResource(key);
                Expect(Templates.Tree != null && Templates.Item != null && Templates.Type != null, "row templates load");
            });
            UiRound();
            Step("Remove the test drive", () => Diskpart($"select vdisk file=\"{_vhd}\"", "detach vdisk"));

            _log.AppendLine(Errors.Count == 0 ? "SELF-TEST PASSED" : $"SELF-TEST FAILED ({Errors.Count})");
            try { File.WriteAllText(Path.Combine(Path.GetDirectoryName(AppInfo.ExePath) ?? ".", "self-test.log"), _log.ToString()); }
            catch { /* read-only folder */ }
            try { Directory.Delete(_temp, true); }
            catch { /* ignore */ }
            return Errors.Count == 0 ? 0 : 1;
        }

        // ================================================================== helpers

        private static void Step(string name, Action action)
        {
            var before = Errors.Count;
            var started = DateTime.UtcNow;
            try
            {
                action();
                Pump();
                if (Errors.Count > before) throw new Exception(string.Join(" | ", Errors.Skip(before)));
                _log.AppendLine($"ok    {name} ({(DateTime.UtcNow - started).TotalSeconds:0.0}s)");
            }
            catch (Exception ex)
            {
                if (Errors.Count == before) Errors.Add(name + ": " + ex.Message);
                _log.AppendLine("FAIL  " + name + ": " + ex);
            }
        }

        private static void Note(string text) => _log.AppendLine("      " + text);

        private static void Expect(bool condition, string what)
        {
            if (!condition) throw new Exception("expected " + what);
        }

        private static void WaitFor(Func<bool> condition, int seconds, string what)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (!condition())
            {
                if (DateTime.UtcNow > until) throw new Exception("timed out waiting for " + what);
                Pump();
                Thread.Sleep(50);
            }
        }

        private static void Wait(Task task, int seconds, string what)
        {
            WaitFor(() => task.IsCompleted, seconds, what);
            if (task.IsFaulted) throw task.Exception.InnerException ?? task.Exception;
        }

        private static void Pump()
        {
            foreach (Window w in Application.Current.Windows) w.UpdateLayout();
            Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Thread.Sleep(20);
            Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        }

        private static void OffScreen(Window w)
        {
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.WindowState = WindowState.Normal;
            w.Left = -20000;
            w.Top = -20000;
            w.ShowActivated = false;
            w.ShowInTaskbar = false;
        }

        private static void ShowAndClose(Window w)
        {
            OffScreen(w);
            w.Show();
            Pump();
            w.Close();
        }

        private static (int Code, string Output) Exec(string file, string args)
        {
            var info = new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = Process.Start(info))
            {
                var output = p.StandardOutput.ReadToEndAsync();
                var error = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(180000))
                {
                    p.Kill();
                    throw new Exception(Path.GetFileName(file) + " didn't finish");
                }
                return (p.ExitCode, output.Result + error.Result);
            }
        }

        private static void Run(string file, string args)
        {
            var (code, output) = Exec(file, args);
            if (code != 0) throw new Exception($"{file} {args} failed ({code}): {output}");
        }

        private static void Diskpart(params string[] commands)
        {
            var script = Path.Combine(_temp, "diskpart-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".txt");
            File.WriteAllLines(script, commands);
            Run("diskpart.exe", "/s \"" + script + "\"");
        }

        private static byte[] Pattern(int length, int seed)
        {
            var data = new byte[length];
            var x = (uint)seed * 2654435761u + 1;
            for (var i = 0; i < length; i++)
            {
                x ^= x << 13;
                x ^= x >> 17;
                x ^= x << 5;
                data[i] = (byte)x;
            }
            return data;
        }

        private static void Put(string relative, byte[] data)
        {
            var path = Path.Combine(Data, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, data);
        }

        // ================================================================== the test drive

        private static readonly Dictionary<string, long> Sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        private static void CreateDrive()
        {
            var used = new HashSet<char>(DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])));
            var letter = "RSTUVWXYZQPONMLKJ".First(c => !used.Contains(c));
            _drive = letter + ":";
            _vhd = Path.Combine(_temp, "kudutree-test.vhdx");
            Diskpart($"create vdisk file=\"{_vhd}\" maximum=400 type=expandable", $"select vdisk file=\"{_vhd}\"", "attach vdisk",
                "create partition primary", "format fs=ntfs quick label=KTTEST", $"assign letter={letter}");
            WaitFor(() => Directory.Exists(_drive + "\\"), 30, "drive " + _drive);

            void Add(string relative, int length, int seed)
            {
                Put(relative, Pattern(length, seed));
                Sizes[relative] = length;
            }
            Add(@"Photos\IMG_0001.jpg", 2_400_000, 1);
            Add(@"Photos\IMG_0002.jpg", 1_900_000, 2);
            Add(@"Photos\2026\IMG_0100.jpg", 3_100_000, 3);
            Add(@"Videos\holiday.mp4", 40_000_000, 4);
            Add(@"Docs\report.pdf", 150_000, 5);
            Add(@"Docs\notes.txt", 120, 6);                // small enough to live inside the file index
            Add(@"Docs\Old\2019\tax.pdf", 70_000, 7);
            Add(@"Docs\Old\2019\receipt.pdf", 30_000, 8);
            Directory.CreateDirectory(Path.Combine(Data, "Empty folder", "inside"));
            var longAgo = DateTime.Now.AddYears(-2);
            File.SetLastWriteTime(Path.Combine(Data, @"Videos\holiday.mp4"), longAgo);
            File.SetLastAccessTime(Path.Combine(Data, @"Videos\holiday.mp4"), longAgo);

            // A compressed file: very compressible content, then NTFS compression.
            var text = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("KUDUTREE compresses well. ", 200_000)));
            Put(@"Logs\big.log", text);
            Sizes[@"Logs\big.log"] = text.Length;
            Run("compact.exe", $"/c /q \"{Path.Combine(Data, @"Logs\big.log")}\"");

            // A sparse file: 200 MB long, almost nothing stored.
            var sparse = Path.Combine(Data, @"Logs\sparse.bin");
            Run("fsutil.exe", $"file createnew \"{sparse}\" 0");
            Run("fsutil.exe", $"sparse setflag \"{sparse}\"");
            using (var f = new FileStream(sparse, FileMode.Open, FileAccess.Write)) f.SetLength(200L * 1024 * 1024);
            Sizes[@"Logs\sparse.bin"] = 200L * 1024 * 1024;

            // A hard link (a second name for the same file) and a junction (a link to a folder).
            Run("fsutil.exe", $"hardlink create \"{Path.Combine(Data, @"Docs\report (link).pdf")}\" \"{Path.Combine(Data, @"Docs\report.pdf")}\"");
            Sizes[@"Docs\report (link).pdf"] = 150_000;
            Run("cmd.exe", $"/c mklink /J \"{Path.Combine(Data, "Link to Photos")}\" \"{Path.Combine(Data, "Photos")}\"");

            // Make sure everything is written to the drive before the fast scan reads it.
            Run("fsutil.exe", $"volume flush {_drive}");
            Thread.Sleep(1000);
            Note($"test drive {_drive}");
        }

        private static Node Find(ScanResult scan, string relative)
        {
            var node = Queries.Find(scan.Root, Path.Combine(Data, relative));
            if (node == null) throw new Exception($"{relative} wasn't found by the {scan.Method}");
            return node;
        }

        private static void CheckScan(ScanResult scan)
        {
            foreach (var pair in Sizes)
                Expect(Find(scan, pair.Key).Size == pair.Value, $"{pair.Key} is {pair.Value:N0} bytes ({scan.Method}: {Find(scan, pair.Key).Size:N0})");
            var photo = Find(scan, @"Photos\IMG_0001.jpg");
            Expect(photo.Alloc == (2_400_000 + scan.ClusterSize - 1) / scan.ClusterSize * scan.ClusterSize, $"space on disk is whole clusters ({photo.Alloc:N0})");
            var log = Find(scan, @"Logs\big.log");
            Expect(log.Alloc > 0 && log.Alloc < log.Size / 4, $"the compressed file takes much less space ({log.Alloc:N0} of {log.Size:N0})");
            var sparse = Find(scan, @"Logs\sparse.bin");
            Expect(sparse.Alloc < 1024 * 1024, $"the sparse file takes almost nothing ({sparse.Alloc:N0})");
            var report = Find(scan, @"Docs\report.pdf");
            var link = Find(scan, @"Docs\report (link).pdf");
            Expect(report.Alloc + link.Alloc == (150_000 + scan.ClusterSize - 1) / scan.ClusterSize * scan.ClusterSize && (report.Alloc == 0 || link.Alloc == 0),
                $"the hard-linked file's space is counted once ({report.Alloc:N0} + {link.Alloc:N0})");
            var junction = Find(scan, "Link to Photos");
            Expect(junction.IsDir && junction.Files == 0 && junction.Alloc < 64 * 1024, "the junction isn't followed");
            var docs = Find(scan, "Docs");
            Expect(docs.Files == 5 && docs.Folders == 2, $"Docs has 5 files in 2 folders ({docs.Files}, {docs.Folders})");
            Expect(docs.Size == 150_000 * 2 + 120 + 70_000 + 30_000, $"Docs' size adds up ({docs.Size:N0})");
            Expect(Find(scan, "Empty folder").Files == 0, "empty folder");
            var types = scan.Types.All.ToDictionary(t => t.Extension);
            Expect(types[".jpg"].Count == 3 && types[".jpg"].Size == 2_400_000 + 1_900_000 + 3_100_000, "file types count the photos");
        }

        private static ScanResult WalkScan()
        {
            var scan = FolderScanner.Scan(_drive + "\\");
            CheckScan(scan);
            Expect(Find(scan, "Link to Photos").Has(NodeFlags.Link), "the junction is marked as a link");
            Note($"folder listing: {scan.Root.Files:N0} files, {Format.Size(scan.Root.Alloc)} on disk, {scan.Duration.TotalMilliseconds:0} ms");
            return scan;
        }

        private static ScanResult FastScan()
        {
            ScanResult scan;
            using (var device = VolumeDevice.Open(_drive))
                scan = MftScanner.Scan(device, _drive + "\\");
            CheckScan(scan);
            Expect(Find(scan, @"Docs\notes.txt").Alloc == 0, "a tiny file inside the index takes no clusters");
            Expect(scan.Root.Children.Any(c => c.Name == "$MFT" && c.Alloc > 0), "the file index itself is counted");
            Note($"fast scan: {scan.Root.Files:N0} files, {Format.Size(scan.Root.Alloc)} on disk, {scan.Duration.TotalMilliseconds:0} ms");
            return scan;
        }

        private static void Compare(ScanResult walk, ScanResult fast)
        {
            var a = Find(walk, string.Empty);
            var b = Find(fast, string.Empty);
            Expect(a.Files == b.Files && a.Folders == b.Folders, $"same counts ({a.Files}/{a.Folders} and {b.Files}/{b.Folders})");
            Expect(a.Size == b.Size, $"same total size ({a.Size:N0} and {b.Size:N0})");
            var difference = Math.Abs(a.Alloc - b.Alloc);
            Expect(difference <= 64 * 1024, $"the same space on disk, give or take folder indexes and tiny files ({a.Alloc:N0} and {b.Alloc:N0})");
            foreach (var file in Queries.Files(a))
            {
                var other = Queries.Find(b, file.Path);
                Expect(other != null && other.Size == file.Size, file.Path + " has the same size in both");
            }
        }

        private static void SystemDriveScan()
        {
            var system = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            var clock = Stopwatch.StartNew();
            ScanResult scan;
            using (var device = VolumeDevice.Open(system))
                scan = MftScanner.Scan(device, system);
            var drive = new DriveInfo(system);
            var used = drive.TotalSize - drive.TotalFreeSpace;
            Note($"{system} fast scan: {scan.Root.Files:N0} files in {scan.Root.Folders:N0} folders, {Format.Size(scan.Root.Alloc)} on disk of {Format.Size(used)} used, {clock.Elapsed.TotalSeconds:0.0} s");
            Note("biggest on disk: " + string.Join(", ", Queries.Largest(scan.Root, 8, true).Select(n => n.Path + " " + Format.Size(n.Alloc))));
            Note("top folders: " + string.Join(", ", scan.Root.Children.OrderByDescending(c => c.Alloc).Take(8).Select(c => c.Name + " " + Format.Size(c.Alloc))));
            Expect(scan.Root.Files > 50_000, "a real system drive has many files");
            Expect(Queries.Find(scan.Root, Path.Combine(system, "Windows", "System32")) != null, "Windows\\System32 is found");
            Expect(scan.Root.Alloc > used * 0.6 && scan.Root.Alloc < used * 1.1, "the scan accounts for most of the used space");
            var image = Treemap.Render(scan.Root, 1600, 900, true, scan.Types);
            Note($"treemap of {system}: {image.Tiles.Count:N0} rectangles");
        }

        // ================================================================== the window

        private static void UiRound()
        {
            MainWindow main = null;
            Step("Main window: drives", () =>
            {
                main = new MainWindow();
                OffScreen(main);
                Application.Current.MainWindow = main;
                main.Show();
                Pump();
                Expect(main.PageName == "start", "the start page");
            });
            Step("Scan a folder (listing), with the tree growing as it goes", () =>
            {
                Wait(main.StartScanAsync(Data, false), 120, "the scan");
                Expect(main.PageName == "results" && !main.IsScanning, "results shown");
                Expect(main.Scan.Root.Size == Sizes.Values.Sum(), "total size");
                var rows = main.Results.Tree.Rows;
                Expect(rows.Count > 1 && rows[0].Node == main.Scan.Root && rows[1].Node.Name == "Videos", "biggest folder first (" + string.Join(", ", rows.Take(4).Select(r => r.Name)) + ")");
            });
            Step("Treemap", () =>
            {
                WaitFor(() => main.Results.Map.Picture != null, 30, "the treemap");
                var picture = main.Results.Map.Picture;
                var video = Queries.Find(main.Scan.Root, Path.Combine(Data, @"Videos\holiday.mp4"));
                var tile = picture.Bounds(video);
                Expect(tile != null && tile.Value.Node == video, "the video is drawn");
                var hit = picture.HitTest(tile.Value.X + tile.Value.W / 2, tile.Value.Y + tile.Value.H / 2);
                Expect(hit == video, "clicking it finds it");
                main.Results.Select(video, main.Results.Map);
                Pump();
                Expect(main.Results.Tree.SelectedNode == video, "selecting in the treemap selects in the tree");
            });
            Step("File types and largest files", () =>
            {
                Expect(main.Results.TypeRows.Count >= 5 && main.Results.TypeRows[0].Type.Extension == ".mp4", "types, biggest first");
                main.Results.ShowTab("Largest files");
                Pump();
                Expect(main.Results.LargestRows.Count >= 8 && main.Results.LargestRows[0].Node.Name == "holiday.mp4", "largest files");
                main.Results.ShowTab("Folders");
            });
            Step("Size on disk and file size", () =>
            {
                Settings.SizeOnDisk = false;
                RowBase.OnDisk = false;
                main.Results.Tree.Rebuild();
                Expect(main.Results.Tree.Rows[1].Node.Name == "Logs", "by file size the sparse file makes Logs biggest");
                Settings.SizeOnDisk = true;
                RowBase.OnDisk = true;
                main.Results.Tree.Rebuild();
                Expect(main.Results.Tree.Rows[1].Node.Name == "Videos", "on disk the video folder is biggest (" + main.Results.Tree.Rows[1].Name + ")");
            });
            Step("Search", () =>
            {
                Wait(main.Results.SearchFor("*.pdf"), 30, "the search");
                Expect(main.Results.SearchRows.Count == 4, "4 PDFs (with the hard link): " + main.Results.SearchRows.Count);
                Wait(main.Results.SearchFor("IMG", 2), 30, "the search");
                Expect(main.Results.SearchRows.Count == 0, "no photos of 10 MB or more");
                main.Results.ShowTab("Folders");
            });
            Step("Show only one folder, then go up", () =>
            {
                var docs = Queries.Find(main.Scan.Root, Path.Combine(Data, "Docs"));
                main.Results.SetViewRoot(docs);
                Pump();
                Expect(main.Results.Tree.Rows[0].Node == docs, "the folder is the top of the tree");
                main.Results.SetViewRoot(docs.Parent);
                Pump();
            });
            Step("Move a file to the Recycle Bin", () =>
            {
                var tax = Queries.Find(main.Scan.Root, Path.Combine(Data, @"Docs\Old\2019\tax.pdf"));
                var folder = tax.Parent;
                var before = folder.Size;
                Wait(main.DeleteNodeAsync(tax, false, false), 60, "the delete");
                Expect(!File.Exists(Path.Combine(Data, @"Docs\Old\2019\tax.pdf")), "the file is gone");
                Expect(folder.Size == before - 70_000 && !folder.Children.Contains(tax), "the folder's size is updated without scanning again");
            });
            Step("Delete a folder permanently", () =>
            {
                var old = Queries.Find(main.Scan.Root, Path.Combine(Data, @"Docs\Old"));
                var docs = old.Parent;
                Wait(main.DeleteNodeAsync(old, true, false), 60, "the delete");
                Expect(!Directory.Exists(Path.Combine(Data, @"Docs\Old")) && docs.Folders == 0, "the folder is gone");
            });
            Step("Scan one folder again", () =>
            {
                var photos = Queries.Find(main.Scan.Root, Path.Combine(Data, "Photos"));
                Put(@"Photos\new.png", Pattern(500_000, 9));
                Wait(main.RescanFolderAsync(photos), 60, "the folder scan");
                Expect(photos.Files == 4 && photos.Size == 2_400_000 + 1_900_000 + 3_100_000 + 500_000, "the new file is counted");
                Expect(main.Scan.Root.Files == photos.Files + main.Scan.Root.Children.Where(c => c != photos).Sum(c => c.IsDir ? c.Files : 1), "the totals above it follow");
            });
            Step("Old and empty", () =>
            {
                main.Results.ShowTab("Old & empty");
                Pump();
                Expect(main.Results.OldRows.Any(r => r.Node.Name == "holiday.mp4"), "the video not used for two years is listed");
                Expect(main.Results.OldRows.All(r => r.Node.Name != "IMG_0001.jpg"), "a recent file isn't");
                Expect(main.Results.EmptyRows.Count == 1 && main.Results.EmptyRows[0].Node.Name == "Empty folder", "the empty folder (with an empty folder inside) is listed once");
                var moved = main.Results.RecycleEmptyFolders(false);
                Wait(moved, 60, "the empty folders to go");
                Expect(moved.Result == 1 && !Directory.Exists(Path.Combine(Data, "Empty folder")), "moved to the Recycle Bin");
            });
            Step("Duplicates", () =>
            {
                var photo = File.ReadAllBytes(Path.Combine(Data, @"Photos\IMG_0001.jpg"));
                Put(@"Dups\IMG_0001 copy.jpg", photo);
                Put(@"Dups\Old\IMG_0001 (2).jpg", photo);
                var almost = (byte[])photo.Clone();
                almost[almost.Length - 1] ^= 1;
                Put(@"Dups\IMG_0001 edited.jpg", almost); // same size and start, different end
                Wait(main.RescanFolderAsync(main.Scan.Root), 60, "the rescan");
                main.Results.ShowTab("Duplicates");
                Wait(main.Results.FindDuplicates(1), 120, "the duplicate search");
                var rows = main.Results.DuplicateRows;
                Expect(rows.Count == 4 && rows[0].Header && rows.Skip(1).All(r => r.Node != null && r.Node.Name.StartsWith("IMG_0001") && !r.Node.Name.Contains("edited")),
                    "one set of three identical photos: " + string.Join(", ", rows.Select(r => r.Name)));
                Wait(main.DeleteNodeAsync(rows[3].Node, false, false), 60, "the delete");
                Expect(main.Results.DuplicateRows.Count == 3, "a deleted copy leaves the set");
            });
            Step("Save, open and compare scans", () =>
            {
                var file = Path.Combine(_temp, "Data" + SavedScan.Extension);
                Wait(main.SaveScanToAsync(file), 60, "the save");
                Expect(File.Exists(file) && SavedScan.ReadInfo(file).Files == main.Scan.Root.Files, "saved");
                Put(@"Downloads\setup.exe", Pattern(3_000_000, 10));
                File.Delete(Path.Combine(Data, @"Videos\holiday.mp4"));
                Wait(main.StartScanAsync(Data, false), 120, "the new scan");
                main.Results.ShowTab("Changes");
                Wait(main.Results.CompareWith(file), 60, "the comparison");
                var c = main.Results.LastComparison;
                Expect(c != null && c.Changes.Count > 0, "changes found");
                Expect(c.Changes.Any(x => x.Kind == ChangeKind.Removed && x.Path.EndsWith("holiday.mp4")), "the deleted video is listed");
                Expect(Math.Abs(c.Changes[0].Delta) >= 40_000_000, "biggest change first: " + c.Changes[0].Path);
                Expect(c.Changes.Any(x => x.Kind == ChangeKind.New && x.Path.EndsWith("Downloads") && x.After >= 3_000_000), "the new folder is listed");
                Expect(main.Results.ChangeRows.Count == c.Changes.Count, "all listed");
                Wait(main.OpenSavedScanAsync(file), 60, "opening the saved scan");
                Expect(main.Scan.SavedAs == file && Queries.Find(main.Scan.Root, Path.Combine(Data, @"Videos\holiday.mp4")) != null, "the saved scan shows how it was");
            });
            Step("Fast scan from the window", () =>
            {
                Wait(main.StartScanAsync(_drive + "\\", true), 120, "the fast scan");
                Expect(main.Scan.Fast && main.PageName == "results", "fast scan results");
                WaitFor(() => main.Results.Map.Picture != null && !main.Results.Map.IsRendering, 30, "the treemap");
            });
            Step("Dark theme", () =>
            {
                ThemeManager.Apply(ThemeMode.Dark);
                main.Results.ShowTab("Largest files");
                Pump();
                main.Results.ShowTab("Search");
                Pump();
                foreach (var tab in new[] { "Old & empty", "Duplicates", "Changes" })
                {
                    main.Results.ShowTab(tab);
                    Pump();
                }
                main.Results.ShowTab("Folders");
                main.ShowStart();
                Pump();
                ThemeManager.Apply(ThemeMode.Light);
                Pump();
            });
            Step("Settings window", () => ShowAndClose(new SettingsWindow(main)));
            Step("About window", () => ShowAndClose(About.Build(main)));
            Step("Close main window", () =>
            {
                main?.Close();
                Pump();
            });
        }
    }
}
