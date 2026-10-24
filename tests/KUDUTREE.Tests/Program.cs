using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Kudutree.Core;

namespace Kudutree.Tests
{
    public static class Program
    {
        private static int _failed;
        private static int _passed;
        private static string _root;

        public static int Main()
        {
            _root = Path.Combine(Path.GetTempPath(), "kudutree-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_root);
            try
            {
                foreach (var m in typeof(Program).GetMethods(BindingFlags.NonPublic | BindingFlags.Static).Where(m => m.Name.StartsWith("Test")))
                {
                    try
                    {
                        m.Invoke(null, null);
                        _passed++;
                        Console.WriteLine("PASS " + m.Name);
                    }
                    catch (TargetInvocationException ex)
                    {
                        _failed++;
                        Console.WriteLine("FAIL " + m.Name + ": " + ex.InnerException);
                    }
                }
            }
            finally
            {
                try { Directory.Delete(_root, true); }
                catch { /* ignore */ }
            }
            Console.WriteLine($"{_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }

        private static void Check(bool condition, string what)
        {
            if (!condition) throw new Exception("Check failed: " + what);
        }

        private static void Equal<T>(T expected, T actual, string what)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{what}: expected {expected}, got {actual}");
        }

        private static Node Child(Node folder, string name) => folder.Children.Single(c => c.Name == name);

        // ================================================================== fast scan (file index)

        private const int C = NtfsImage.Cluster;

        private static NtfsImage.Record R(long n, bool dir = false, int seq = 1) => new NtfsImage.Record { Number = n, InUse = true, Directory = dir, Sequence = seq };

        /// <summary>A non-resident attribute the way NTFS writes compressed or sparse ones: with the "really used" size at 0x40.</summary>
        private static byte[] CompressedData(long lcn, long clusters, long dataSize, long usedBytes)
        {
            var runs = NtfsImage.EncodeRuns(new[] { (lcn, clusters) });
            var length = (0x48 + runs.Length + 7) & ~7;
            var a = new byte[length];
            BitConverter.GetBytes(0x80).CopyTo(a, 0);
            BitConverter.GetBytes(length).CopyTo(a, 4);
            a[8] = 1;
            BitConverter.GetBytes((ushort)0x48).CopyTo(a, 10);
            BitConverter.GetBytes((ushort)0x0001).CopyTo(a, 12); // compressed
            BitConverter.GetBytes(clusters - 1).CopyTo(a, 24);
            BitConverter.GetBytes((ushort)0x48).CopyTo(a, 32);
            BitConverter.GetBytes((ushort)4).CopyTo(a, 34);
            BitConverter.GetBytes(clusters * C).CopyTo(a, 40);
            BitConverter.GetBytes(dataSize).CopyTo(a, 48);
            BitConverter.GetBytes(dataSize).CopyTo(a, 56);
            BitConverter.GetBytes(usedBytes).CopyTo(a, 0x40);
            runs.CopyTo(a, 0x48);
            return a;
        }

        private static ScanResult FastScanOfTestDrive()
        {
            var img = new NtfsImage();

            var photos = R(20, dir: true);
            photos.Attributes.Add(NtfsImage.StandardInformation(0x10));
            photos.Attributes.Add(NtfsImage.FileName(5, 5, "Photos"));
            photos.Attributes.Add(NtfsImage.NonResident(0xA0, new[] { (60L, 1L) }, C, name: "$I30"));
            img.Add(photos);

            var a = R(21);
            a.Attributes.Add(NtfsImage.StandardInformation());
            a.Attributes.Add(NtfsImage.FileName(20, 1, "a.jpg"));
            a.Attributes.Add(NtfsImage.NonResident(0x80, new[] { (61L, 3L) }, 10000));
            img.Add(a);

            var tiny = R(22);
            tiny.Attributes.Add(NtfsImage.StandardInformation());
            tiny.Attributes.Add(NtfsImage.FileName(20, 1, "tiny.txt"));
            tiny.Attributes.Add(NtfsImage.Resident(0x80, new byte[50]));
            img.Add(tiny);

            var y2026 = R(23, dir: true);
            y2026.Attributes.Add(NtfsImage.StandardInformation(0x10));
            y2026.Attributes.Add(NtfsImage.FileName(20, 1, "2026"));
            img.Add(y2026);

            var b = R(24);
            b.Attributes.Add(NtfsImage.StandardInformation());
            b.Attributes.Add(NtfsImage.FileName(23, 1, "b.jpg"));
            b.Attributes.Add(NtfsImage.FileName(20, 1, "copy of b.jpg")); // a hard link in Photos
            b.Attributes.Add(NtfsImage.NonResident(0x80, new[] { (64L, 2L) }, 8000));
            img.Add(b);

            var longName = R(25);
            longName.Attributes.Add(NtfsImage.StandardInformation());
            longName.Attributes.Add(NtfsImage.FileName(5, 5, "LONGFI~1.DOC", 2));
            longName.Attributes.Add(NtfsImage.FileName(5, 5, "Long file name.docx", 1));
            longName.Attributes.Add(NtfsImage.Resident(0x80, new byte[100]));
            img.Add(longName);

            var compressed = R(26);
            compressed.Attributes.Add(NtfsImage.StandardInformation(0x800));
            compressed.Attributes.Add(NtfsImage.FileName(5, 5, "compressed.log"));
            compressed.Attributes.Add(CompressedData(70, 10, 40000, 2 * C));
            img.Add(compressed);

            var deleted = new NtfsImage.Record { Number = 27, InUse = false };
            deleted.Attributes.Add(NtfsImage.FileName(5, 5, "deleted.txt"));
            deleted.Attributes.Add(NtfsImage.NonResident(0x80, new[] { (90L, 5L) }, 20000));
            img.Add(deleted);

            var ads = R(28);
            ads.Attributes.Add(NtfsImage.StandardInformation());
            ads.Attributes.Add(NtfsImage.FileName(5, 5, "with stream.txt"));
            ads.Attributes.Add(NtfsImage.Resident(0x80, new byte[10]));
            ads.Attributes.Add(NtfsImage.NonResident(0x80, new[] { (95L, 1L) }, 3000, name: "Zone.Identifier"));
            img.Add(ads);

            var orphan = R(29);
            orphan.Attributes.Add(NtfsImage.StandardInformation());
            orphan.Attributes.Add(NtfsImage.FileName(40, 1, "orphan.bin"));
            orphan.Attributes.Add(NtfsImage.Resident(0x80, new byte[7]));
            img.Add(orphan);

            var stale = R(30);
            stale.Attributes.Add(NtfsImage.StandardInformation());
            stale.Attributes.Add(NtfsImage.FileName(23, 9, "stale parent.bin"));
            stale.Attributes.Add(NtfsImage.Resident(0x80, new byte[3]));
            img.Add(stale);

            var split = R(31);
            split.Attributes.Add(NtfsImage.StandardInformation());
            split.Attributes.Add(NtfsImage.AttributeList((0x30, 31, 0), (0x80, 32, 0)));
            split.Attributes.Add(NtfsImage.FileName(23, 1, "big split.iso"));
            img.Add(split);
            var extension = new NtfsImage.Record { Number = 32, InUse = true, BaseRecord = 31 };
            extension.Attributes.Add(NtfsImage.NonResident(0x80, new[] { (100L, 5L) }, 20000));
            img.Add(extension);

            var sparse = R(33);
            sparse.Attributes.Add(NtfsImage.StandardInformation(0x200));
            sparse.Attributes.Add(NtfsImage.FileName(5, 5, "sparse.vhd"));
            sparse.Attributes.Add(NtfsImage.NonResident(0x80, new[] { (110L, 2L), (-1L, 100000L) }, 100002L * C, flags: 0x8000));
            img.Add(sparse);

            var path = img.Save(Path.Combine(_root, "test-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".img"));
            using (var device = new ImageDevice(path))
            {
                Check(MftScanner.IsNtfs(device), "recognised as NTFS");
                return MftScanner.Scan(device, "T:\\");
            }
        }

        private static void TestFastScanTree()
        {
            var scan = FastScanOfTestDrive();
            var root = scan.Root;
            Equal("T:\\", root.Name, "root name");
            Check(scan.Fast, "fast");
            var photos = Child(root, "Photos");
            var y2026 = Child(photos, "2026");
            Equal(10000L, Child(photos, "a.jpg").Size, "file size");
            Equal(3L * C, Child(photos, "a.jpg").Alloc, "space on disk");
            Equal(50L, Child(photos, "tiny.txt").Size, "small file size");
            Equal(0L, Child(photos, "tiny.txt").Alloc, "a file inside the index takes no clusters");
            Equal(8000L, Child(y2026, "b.jpg").Size, "hard-linked file");
            Equal(2L * C, Child(y2026, "b.jpg").Alloc, "first name counts the space");
            var link = Child(photos, "copy of b.jpg");
            Check(link.Has(NodeFlags.HardLink) && link.Size == 8000 && link.Alloc == 0, "second name: same size, no extra space");
            Check(root.Children.Any(c => c.Name == "Long file name.docx") && root.Children.All(c => c.Name != "LONGFI~1.DOC"), "long name used, not the 8.3 one");
            var compressed = Child(root, "compressed.log");
            Check(compressed.Size == 40000 && compressed.Alloc == 2 * C, $"compressed: size 40000, uses 8 KB ({compressed.Size}, {compressed.Alloc})");
            Check(root.Children.All(c => c.Name != "deleted.txt"), "deleted files aren't counted");
            var ads = Child(root, "with stream.txt");
            Check(ads.Size == 10 && ads.Alloc == C, "alternate data stream counts as space, not size");
            var lost = Child(root, "(Files without a folder)");
            Check(lost.Children.Any(c => c.Name == "orphan.bin") && lost.Children.Any(c => c.Name == "stale parent.bin"), "files whose folder is gone");
            var split = Child(y2026, "big split.iso");
            Check(split.Size == 20000 && split.Alloc == 5 * C, "size from an extension record");
            var sparse = Child(root, "sparse.vhd");
            Check(sparse.Size == 100002L * C && sparse.Alloc == 2 * C, $"sparse: only the clusters really there count ({sparse.Alloc})");
            Check(Child(root, "$MFT").Alloc == NtfsImage.MftRecords * NtfsImage.RecordSize, "the file index itself is counted");

            Equal(C + 3L * C + 0 + 0 + 2L * C + 5L * C, photos.Alloc, "folder total includes its own index and everything inside");
            Equal(10000L + 50 + 8000 + 8000 + 20000, photos.Size, "folder size (the hard link shows its size too, like File Explorer)");
            Equal(5, photos.Files, "files in Photos (with the hard link)");
            Equal(1, photos.Folders, "folders in Photos");
            Equal(root.Children.Sum(c => c.Alloc), root.Alloc, "root total");
            Equal(root.Children.Sum(c => c.IsDir ? c.Files : 1), root.Files, "root file count");
        }

        // ================================================================== folder listing

        private static string MakeTree()
        {
            var dir = Path.Combine(_root, "tree-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            void Put(string relative, int bytes)
            {
                var p = Path.Combine(dir, relative.Replace('\\', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                File.WriteAllBytes(p, new byte[bytes]);
                File.SetLastWriteTimeUtc(p, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(bytes % 300));
            }
            Put(@"Videos\holiday.mp4", 900_000);
            Put(@"Videos\clip.mp4", 300_000);
            Put(@"Docs\report.pdf", 50_000);
            Put(@"Docs\notes.txt", 1_000);
            Put(@"Docs\Old\2019\tax.pdf", 20_000);
            Put(@"empty.txt", 0);
            Directory.CreateDirectory(Path.Combine(dir, "Empty folder"));
            return dir;
        }

        private static void TestFolderScan()
        {
            var dir = MakeTree();
            var progress = new ScanProgress();
            var scan = FolderScanner.Scan(dir, progress);
            var root = scan.Root;
            Equal(1_271_000L, root.Size, "total size");
            Equal(6, root.Files, "files");
            Equal(5, root.Folders, "folders");
            Equal(6L, progress.Files, "progress files");
            Equal(1_271_000L, progress.Bytes, "progress bytes");
            var docs = Child(root, "Docs");
            Equal(71_000L, docs.Size, "folder size");
            Equal(3, docs.Files, "files in Docs");
            Equal(2, docs.Folders, "folders in Docs");
            Equal(4096L, Child(docs, "notes.txt").Alloc, "space is whole clusters");
            Equal(0L, Child(root, "Empty folder").Mtime, "a folder with no files has no date");
            Equal(0L, Child(root, "empty.txt").Alloc, "empty file takes nothing");
            Check(root.Children.All(c => !c.IsDir || c.Has(NodeFlags.Listed)), "every folder listed");
            Equal(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(900_000 % 300).Ticks, Child(root, "Videos").Mtime, "folder date is its newest file");
            Equal(dir + Path.DirectorySeparatorChar + "Docs" + Path.DirectorySeparatorChar + "Old", Child(docs, "Old").Path, "full path");
            Equal(Child(docs, "Old"), Queries.Find(root, Path.Combine(dir, "Docs", "Old")), "find by path");
            Equal(2, scan.Types.All.Single(t => t.Extension == ".pdf").Count, "file types counted");
            Check(scan.Types.All.Single(t => t.Extension == ".mp4").Color == 0, "biggest type gets the first colour");
            Equal("Video", scan.Types.All.Single(t => t.Extension == ".mp4").Group, "group");
        }

        private static void TestFolderLinksNotFollowed()
        {
            if (Paths.IsWindows) return; // the Windows self-test checks junctions
            var dir = MakeTree();
            Directory.CreateSymbolicLink(Path.Combine(dir, "link to Docs"), Path.Combine(dir, "Docs"));
            var scan = FolderScanner.Scan(dir);
            var link = Child(scan.Root, "link to Docs");
            Check(link.Has(NodeFlags.Link) && link.Children.Count == 0 && link.Size == 0, "a link isn't followed");
            Equal(1_271_000L, scan.Root.Size, "nothing counted twice");
        }

        private static void TestMissingFolder()
        {
            try
            {
                FolderScanner.Scan(Path.Combine(_root, "no such folder"));
                throw new Exception("accepted");
            }
            catch (DirectoryNotFoundException) { }
        }

        // ================================================================== queries

        private static void TestLargestAndSearch()
        {
            var scan = FolderScanner.Scan(MakeTree());
            var largest = Queries.Largest(scan.Root, 3, false);
            Check(largest.Select(n => n.Name).SequenceEqual(new[] { "holiday.mp4", "clip.mp4", "report.pdf" }), "largest: " + string.Join(", ", largest.Select(n => n.Name)));

            List<string> Names(SearchQuery q) => Queries.Search(scan.Root, q, out _).Select(n => n.Name).ToList();
            Check(Names(new SearchQuery { Text = "*.pdf", OnDisk = false }).SequenceEqual(new[] { "report.pdf", "tax.pdf" }), "wildcard");
            Check(Names(new SearchQuery { Text = "OLI" }).SequenceEqual(new[] { "holiday.mp4" }), "part of a name, any case");
            Check(Names(new SearchQuery { Text = "*.mp4; *.txt", OnDisk = false, MinSize = 1 }).SequenceEqual(new[] { "holiday.mp4", "clip.mp4", "notes.txt" }), "several patterns and a minimum size");
            Check(Names(new SearchQuery { Kind = FindKind.Folders, OnDisk = false }).First() == "Videos", "folders only");
            Equal(6, Names(new SearchQuery { Kind = FindKind.Files }).Count, "files only");
            var old = Names(new SearchQuery { Kind = FindKind.Files, ChangedBefore = new DateTime(2024, 1, 2) });
            Check(old.OrderBy(n => n).SequenceEqual(new[] { "clip.mp4", "empty.txt", "holiday.mp4" }), "changed before: " + string.Join(", ", old));
            Queries.Search(scan.Root, new SearchQuery { Limit = 2 }, out var total);
            Equal(11, total, "total matches counted beyond the limit");
        }

        private static void TestRemoveUpdatesTotals()
        {
            var scan = FolderScanner.Scan(MakeTree());
            var root = scan.Root;
            var docs = Child(root, "Docs");
            var old = Child(docs, "Old");
            Queries.Remove(scan, old);
            Equal(51_000L, docs.Size, "folder size after delete");
            Equal(2, docs.Files, "files after delete");
            Equal(0, docs.Folders, "folders after delete");
            Equal(1_251_000L, root.Size, "root size after delete");
            Equal(5, root.Files, "root files after delete");
            Equal(3, root.Folders, "root folders after delete");
            Check(old.Has(NodeFlags.Deleted) && !docs.Children.Contains(old), "removed from the tree");
            Equal(1, scan.Types.All.Single(t => t.Extension == ".pdf").Count, "file types after delete");
            Queries.Remove(scan, Child(Child(root, "Videos"), "clip.mp4"));
            Equal(1, scan.Types.All.Single(t => t.Extension == ".mp4").Count, "file delete");
            Equal(900_000L, Child(root, "Videos").Size, "file delete size");
        }

        // ================================================================== treemap

        private static void TestTreemapLayout()
        {
            var sizes = new List<double> { 500, 250, 120, 60, 40, 20, 6, 3, 1 };
            foreach (var (w, h) in new[] { (800.0, 400.0), (300.0, 700.0), (100.0, 100.0) })
            {
                var rects = Treemap.Layout(sizes, 10, 20, w, h);
                Equal(sizes.Count, rects.Count, "one rectangle each");
                var total = sizes.Sum();
                for (var i = 0; i < sizes.Count; i++)
                {
                    var r = rects[i];
                    var expected = sizes[i] / total * w * h;
                    Check(Math.Abs(r.W * r.H - expected) < expected * 1e-6 + 1e-6, $"area of {i} matches its size ({r.W * r.H} vs {expected})");
                    Check(r.X >= 10 - 1e-9 && r.Y >= 20 - 1e-9 && r.X + r.W <= 10 + w + 1e-6 && r.Y + r.H <= 20 + h + 1e-6, $"rectangle {i} inside the space");
                }
                for (var i = 0; i < rects.Count; i++)
                for (var j = i + 1; j < rects.Count; j++)
                {
                    var a = rects[i];
                    var b = rects[j];
                    var overlap = Math.Max(0, Math.Min(a.X + a.W, b.X + b.W) - Math.Max(a.X, b.X)) * Math.Max(0, Math.Min(a.Y + a.H, b.Y + b.H) - Math.Max(a.Y, b.Y));
                    Check(overlap < 1e-6, $"rectangles {i} and {j} don't overlap");
                }
                Check(Math.Max(rects[0].W / rects[0].H, rects[0].H / rects[0].W) < 3, "the biggest rectangle is nearly square");
            }
        }

        private static void TestTreemapRender()
        {
            var scan = FolderScanner.Scan(MakeTree());
            var image = Treemap.Render(scan.Root, 400, 300, false, scan.Types);
            Equal(400 * 300, image.Pixels.Length, "pixels");
            var holiday = Child(Child(scan.Root, "Videos"), "holiday.mp4");
            var bounds = image.Bounds(holiday);
            Check(bounds != null, "the biggest file is drawn");
            var t = bounds.Value;
            var share = (double)(t.W * t.H) / (400 * 300);
            Check(Math.Abs(share - 900_000.0 / 1_271_000) < 0.02, $"its area matches its share ({share:0.000})");
            Equal(holiday, image.HitTest(t.X + t.W / 2, t.Y + t.H / 2), "clicking it finds it");
            var pixel = image.Pixels[(t.Y + t.H / 2) * 400 + t.X + t.W / 2];
            Check(((pixel >> 16) & 0xFF) < ((pixel) & 0xFF), "drawn in its type's colour (blue for the biggest type)");
            Check(image.Bounds(Child(Child(Child(scan.Root, "Docs"), "Old"), "2019")) != null, "small items fall back to a drawn folder");
        }

        // ================================================================== small things

        private static void TestPathsAndFormat()
        {
            Check(Paths.IsDriveRoot("C:\\") && !Paths.IsDriveRoot("C:\\Users"), "drive root");
            Check(Paths.IsUnc(@"\\nas\share") && !Paths.IsUnc(@"\\?\C:\x"), "unc");
            Equal("1.5 GB", Format.Size(1610612736), "size text");
            Equal("0 B", Format.Size(0), "zero");
            Check(Queries.Matcher("IMG_????.jpg")("img_0001.JPG") && !Queries.Matcher("IMG_????.jpg")("IMG_01.jpg"), "? pattern");
        }
    }
}
