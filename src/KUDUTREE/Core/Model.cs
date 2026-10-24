using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Kudutree.Core
{
    [Flags]
    public enum NodeFlags : byte
    {
        None = 0,
        /// <summary>A folder whose contents have been listed (while scanning, others show as "…").</summary>
        Listed = 1,
        /// <summary>A folder that couldn't be read (access denied, for example).</summary>
        Unreadable = 2,
        /// <summary>A link to another folder (junction or symbolic link); not followed, so it counts nothing.</summary>
        Link = 4,
        /// <summary>A second name (hard link) of a file counted elsewhere; it takes no extra space on the drive.</summary>
        HardLink = 8,
        Deleted = 16
    }

    /// <summary>
    /// A file or folder found by a scan. Folder totals (sizes, counts, newest change) include everything inside. The fields
    /// are plain fields (not properties) so scanners can update them with Interlocked while the window reads them.
    /// </summary>
    public sealed class Node
    {
        public string Name;
        public Node Parent;
        /// <summary>The folder's contents; null for files.</summary>
        public List<Node> Children;
        /// <summary>File size (what File Explorer shows).</summary>
        public long Size;
        /// <summary>Space taken on the drive: compressed, sparse and online-only files take less, hard links count once.</summary>
        public long Alloc;
        /// <summary>Last modified, UTC ticks. Folders: the newest file inside (0 for a folder with no files).</summary>
        public long Mtime;
        /// <summary>Files: last opened, UTC ticks (Windows may update this only roughly, or not at all).</summary>
        public long Atime;
        /// <summary>Folders: files inside, at any depth.</summary>
        public int Files;
        /// <summary>Folders: folders inside, at any depth.</summary>
        public int Folders;
        public FileAttributes Attributes;
        /// <summary>Files: index into the scan's file types.</summary>
        public int Type = -1;
        public NodeFlags Flags;

        public bool IsDir => Children != null;
        public bool Has(NodeFlags flag) => (Flags & flag) != 0;
        public long Measure(bool onDisk) => onDisk ? Alloc : Size;

        public string Path
        {
            get
            {
                if (Parent == null) return Name;
                var parts = new List<string>();
                for (var n = this; n.Parent != null; n = n.Parent) parts.Add(n.Name);
                var root = Root.Name;
                parts.Reverse();
                var sep = System.IO.Path.DirectorySeparatorChar.ToString();
                return root.EndsWith("\\") || root.EndsWith("/") ? root + string.Join(sep, parts) : root + sep + string.Join(sep, parts);
            }
        }

        public Node Root
        {
            get
            {
                var n = this;
                while (n.Parent != null) n = n.Parent;
                return n;
            }
        }

        public int Depth
        {
            get
            {
                var d = 0;
                for (var n = Parent; n != null; n = n.Parent) d++;
                return d;
            }
        }

        public bool IsInside(Node folder)
        {
            for (var n = this; n != null; n = n.Parent)
                if (n == folder) return true;
            return false;
        }

        public string Extension
        {
            get
            {
                if (IsDir) return string.Empty;
                var dot = Name.LastIndexOf('.');
                return dot > 0 && dot < Name.Length - 1 && Name.Length - dot <= 16 ? Name.Substring(dot).ToLowerInvariant() : string.Empty;
            }
        }

        public DateTime Modified => Mtime > 0 ? new DateTime(Mtime, DateTimeKind.Utc).ToLocalTime() : DateTime.MinValue;

        /// <summary>The later of last changed and last opened (files).</summary>
        public DateTime LastUsed => Math.Max(Mtime, Atime) > 0 ? new DateTime(Math.Max(Mtime, Atime), DateTimeKind.Utc).ToLocalTime() : DateTime.MinValue;

        /// <summary>Adds a finished folder listing's totals to a folder and all folders above it (safe from any thread).</summary>
        public void AddUp(long size, long alloc, int files, int folders, long mtime)
        {
            for (var n = this; n != null; n = n.Parent)
            {
                if (size != 0) Interlocked.Add(ref n.Size, size);
                if (alloc != 0) Interlocked.Add(ref n.Alloc, alloc);
                if (files != 0) Interlocked.Add(ref n.Files, files);
                if (folders != 0) Interlocked.Add(ref n.Folders, folders);
                long seen;
                while (mtime > (seen = Interlocked.Read(ref n.Mtime)) && Interlocked.CompareExchange(ref n.Mtime, mtime, seen) != seen) { }
            }
        }

        public override string ToString() => Name + (IsDir ? "\\" : string.Empty);
    }

    /// <summary>Shared progress of a running scan, read by the window on a timer.</summary>
    public sealed class ScanProgress
    {
        public long Files;
        public long Folders;
        public long Bytes;
        /// <summary>Fast scans: file index records read so far, out of <see cref="Total"/>.</summary>
        public long Done;
        public long Total;
        public volatile string Current = string.Empty;
        public volatile string Phase = "Scanning";
    }

    /// <summary>What a scan found.</summary>
    public sealed class ScanResult
    {
        public Node Root;
        public string Path;
        /// <summary>"Fast scan (file index)" or "Folder listing".</summary>
        public string Method;
        public bool Fast;
        public DateTime Started;
        public TimeSpan Duration;
        public int ClusterSize;
        public List<string> Unreadable = new List<string>();
        public FileTypes Types = new FileTypes();
        /// <summary>For a whole drive: its size and free space, to show what the scan accounts for.</summary>
        public long DriveTotal = -1;
        public long DriveFree = -1;
        /// <summary>The file this scan was saved to or opened from.</summary>
        public string SavedAs;

        /// <summary>Counts file types, once the scan has finished.</summary>
        public void Finish()
        {
            Types = FileTypes.Count(Root);
        }
    }
}
