using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Kudutree.Core
{
    /// <summary>A saved scan's details, read without loading the whole tree (for the list of saved scans).</summary>
    public sealed class SavedScanInfo
    {
        public string File;
        public string Path;
        public string Method;
        public DateTime Started;
        public long Size;
        public long Alloc;
        public int Files;
        public bool Fast;
        public TimeSpan Duration;
        public int ClusterSize;
        public long DriveTotal = -1;
        public long DriveFree = -1;
        public List<string> Unreadable = new List<string>();

        public long Measure(bool onDisk) => onDisk ? Alloc : Size;
    }

    /// <summary>
    /// Saving a scan to a file (.kudutree) to look at again later, or to compare with a later scan to see what grew.
    /// Compressed and compact: a scan of a drive with a million files takes a few tens of megabytes.
    /// </summary>
    public static class SavedScan
    {
        public const string Extension = ".kudutree";
        public const string FileFilter = "KUDUTREE scans (*.kudutree)|*.kudutree";
        private const string Magic = "KTS1";

        /// <summary>Where scans are saved unless another place is chosen.</summary>
        public static string Folder => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KUDUTREE", "Scans");

        public static string DefaultName(ScanResult scan)
        {
            var name = scan.Path.TrimEnd('\\', '/');
            if (name.Length == 2 && name[1] == ':') name = "Drive " + name[0];
            else name = System.IO.Path.GetFileName(name);
            var safe = new string(name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
            if (safe.Length == 0) safe = "Scan";
            return safe + " " + scan.Started.ToString("yyyy-MM-dd HHmm") + Extension;
        }

        public static void Save(ScanResult scan, string file)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file));
            var temp = file + ".saving";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write))
            using (var zip = new GZipStream(stream, CompressionLevel.Optimal))
            using (var w = new BinaryWriter(zip, Encoding.UTF8))
            {
                w.Write(Magic);
                w.Write(scan.Path ?? string.Empty);
                w.Write(scan.Method ?? string.Empty);
                w.Write(scan.Fast);
                w.Write(scan.Started.ToBinary());
                w.Write(scan.Duration.Ticks);
                w.Write(scan.ClusterSize);
                w.Write(scan.DriveTotal);
                w.Write(scan.DriveFree);
                w.Write(scan.Root.Size);
                w.Write(scan.Root.Alloc);
                w.Write(scan.Root.Files);
                w.Write(scan.Unreadable.Count);
                foreach (var u in scan.Unreadable) w.Write(u);

                // The tree, folder before its contents; each folder says how many items follow it.
                var stack = new Stack<Node>();
                stack.Push(scan.Root);
                while (stack.Count > 0)
                {
                    var n = stack.Pop();
                    var children = n.IsDir ? n.Children.Where(c => !c.Has(NodeFlags.Deleted)).ToList() : null;
                    w.Write(n.Name ?? string.Empty);
                    w.Write(n.IsDir);
                    w.Write((byte)n.Flags);
                    w.Write((int)n.Attributes);
                    w.Write(n.Size);
                    w.Write(n.Alloc);
                    w.Write(n.Mtime);
                    w.Write(n.Atime);
                    if (children == null) continue;
                    w.Write(n.Files);
                    w.Write(n.Folders);
                    w.Write(children.Count);
                    for (var i = children.Count - 1; i >= 0; i--) stack.Push(children[i]);
                }
            }
            if (File.Exists(file)) File.Delete(file);
            File.Move(temp, file);
        }

        public static ScanResult Load(string file)
        {
            try
            {
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var zip = new GZipStream(stream, CompressionMode.Decompress))
                using (var r = new BinaryReader(zip, Encoding.UTF8))
                {
                    var info = ReadHeader(r, file);
                    var scan = new ScanResult
                    {
                        Path = info.Path,
                        Method = info.Method,
                        Fast = info.Fast,
                        Started = info.Started,
                        Duration = info.Duration,
                        ClusterSize = info.ClusterSize,
                        DriveTotal = info.DriveTotal,
                        DriveFree = info.DriveFree
                    };
                    scan.Unreadable.AddRange(info.Unreadable);

                    var pending = new Stack<(Node Folder, int Left)>();
                    Node root = null;
                    while (true)
                    {
                        var n = new Node { Name = r.ReadString() };
                        var isDir = r.ReadBoolean();
                        n.Flags = (NodeFlags)r.ReadByte() & ~NodeFlags.Deleted;
                        n.Attributes = (FileAttributes)r.ReadInt32();
                        n.Size = r.ReadInt64();
                        n.Alloc = r.ReadInt64();
                        n.Mtime = r.ReadInt64();
                        n.Atime = r.ReadInt64();
                        var count = 0;
                        if (isDir)
                        {
                            n.Files = r.ReadInt32();
                            n.Folders = r.ReadInt32();
                            count = r.ReadInt32();
                            if (count < 0 || count > 50_000_000) throw new InvalidDataException("Damaged scan file.");
                            n.Children = new List<Node>(count);
                            n.Flags |= NodeFlags.Listed;
                        }
                        if (root == null) root = n;
                        else
                        {
                            var (folder, left) = pending.Pop();
                            n.Parent = folder;
                            folder.Children.Add(n);
                            if (left > 1) pending.Push((folder, left - 1));
                        }
                        if (count > 0) pending.Push((n, count));
                        if (pending.Count == 0) break;
                    }
                    scan.Root = root;
                    scan.Finish();
                    return scan;
                }
            }
            catch (Exception ex) when (ex is EndOfStreamException || ex is InvalidDataException || ex is IOException && !(ex is FileNotFoundException) || ex is FormatException)
            {
                throw new InvalidDataException("This scan file can't be read: " + ex.Message);
            }
        }

        private static SavedScanInfo ReadHeader(BinaryReader r, string file)
        {
            if (r.ReadString() != Magic) throw new InvalidDataException("This isn't a KUDUTREE scan file.");
            var info = new SavedScanInfo { File = file, Path = r.ReadString(), Method = r.ReadString() };
            info.Fast = r.ReadBoolean();
            info.Started = DateTime.FromBinary(r.ReadInt64());
            info.Duration = TimeSpan.FromTicks(r.ReadInt64());
            info.ClusterSize = r.ReadInt32();
            info.DriveTotal = r.ReadInt64();
            info.DriveFree = r.ReadInt64();
            info.Size = r.ReadInt64();
            info.Alloc = r.ReadInt64();
            info.Files = r.ReadInt32();
            var count = r.ReadInt32();
            if (count < 0 || count > 1_000_000) throw new InvalidDataException("Damaged scan file.");
            for (var i = 0; i < count; i++) info.Unreadable.Add(r.ReadString());
            return info;
        }

        public static SavedScanInfo ReadInfo(string file)
        {
            try
            {
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var zip = new GZipStream(stream, CompressionMode.Decompress))
                using (var r = new BinaryReader(zip, Encoding.UTF8))
                    return ReadHeader(r, file);
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is FormatException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>Saved scans in the usual folder, newest first; those of <paramref name="path"/> only, if given.</summary>
        public static List<SavedScanInfo> List(string path = null)
        {
            if (!Directory.Exists(Folder)) return new List<SavedScanInfo>();
            return Directory.GetFiles(Folder, "*" + Extension)
                .Select(ReadInfo)
                .Where(i => i != null && (path == null || string.Equals(i.Path.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(i => i.Started)
                .ToList();
        }
    }
}
