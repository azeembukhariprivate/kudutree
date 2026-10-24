using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Kudutree.Core
{
    /// <summary>
    /// Scans a folder by listing it, on several threads at once (which helps most on network shares). Works anywhere
    /// without administrator rights. Each folder's totals are added to the folders above it as soon as it's listed, so
    /// the window can show the tree growing while the scan runs.
    /// </summary>
    public sealed class FolderScanner
    {
        private readonly ScanProgress _progress;
        private readonly CancellationToken _token;
        private readonly ConcurrentQueue<Node> _queue = new ConcurrentQueue<Node>();
        private readonly ConcurrentBag<string> _unreadable = new ConcurrentBag<string>();
        private readonly HashSet<long>[] _seenIds = new HashSet<long>[64];
        private readonly bool _dedupeLinks;
        private readonly long _cluster;
        private int _pending;

        private FolderScanner(string root, ScanProgress progress, CancellationToken token)
        {
            _progress = progress;
            _token = token;
            _cluster = ClusterSize(root);
            _dedupeLinks = Paths.IsWindows && !Paths.IsUnc(root);
            for (var i = 0; i < _seenIds.Length; i++) _seenIds[i] = new HashSet<long>();
        }

        public static ScanResult Scan(string path, ScanProgress progress = null, CancellationToken token = default, Action<ScanResult> started = null)
        {
            progress = progress ?? new ScanProgress();
            path = Paths.Normalize(path);
            if (!Directory.Exists(Paths.Long(path))) throw new DirectoryNotFoundException("The folder " + path + " wasn't found.");
            var result = new ScanResult
            {
                Path = path,
                Method = "Folder listing",
                Started = DateTime.Now,
                Root = new Node { Name = path, Children = new List<Node>(), Attributes = FileAttributes.Directory }
            };
            var scanner = new FolderScanner(path, progress, token);
            result.ClusterSize = (int)scanner._cluster;
            started?.Invoke(result);

            var clock = System.Diagnostics.Stopwatch.StartNew();
            scanner._pending = 1;
            scanner._queue.Enqueue(result.Root);
            var threads = Paths.IsUnc(path) ? 16 : Math.Max(2, Math.Min(8, Environment.ProcessorCount));
            var workers = new Thread[threads];
            for (var i = 0; i < threads; i++)
            {
                workers[i] = new Thread(scanner.Work) { IsBackground = true, Name = "KUDUTREE scan " + i };
                workers[i].Start();
            }
            foreach (var w in workers) w.Join();
            token.ThrowIfCancellationRequested();

            result.Unreadable.AddRange(scanner._unreadable);
            result.Duration = clock.Elapsed;
            result.Finish();
            return result;
        }

        private void Work()
        {
            while (true)
            {
                if (_token.IsCancellationRequested) return;
                if (!_queue.TryDequeue(out var folder))
                {
                    if (Volatile.Read(ref _pending) == 0) return;
                    Thread.Sleep(1);
                    continue;
                }
                try { List(folder); }
                finally { Interlocked.Decrement(ref _pending); }
            }
        }

        private void List(Node folder)
        {
            var path = folder.Path;
            _progress.Current = path;
            List<Node> children;
            try
            {
                children = (Paths.IsWindows ? ListWin32(path) : null) ?? ListManaged(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                folder.Flags |= NodeFlags.Unreadable | NodeFlags.Listed;
                _unreadable.Add(path);
                return;
            }

            long size = 0, alloc = 0, mtime = 0;
            int files = 0, folders = 0;
            foreach (var c in children)
            {
                c.Parent = folder;
                if (c.IsDir)
                {
                    folders++;
                    c.Mtime = 0; // a folder's date is the newest file inside it, filled in as its contents are listed
                    if (c.Has(NodeFlags.Link)) c.Flags |= NodeFlags.Listed;
                    else
                    {
                        Interlocked.Increment(ref _pending);
                        _queue.Enqueue(c);
                    }
                    continue;
                }
                files++;
                size += c.Size;
                alloc += c.Alloc;
                if (c.Mtime > mtime) mtime = c.Mtime;
            }
            folder.Children.AddRange(children);
            Thread.MemoryBarrier(); // the window only reads a folder's contents once it's marked as listed
            folder.Flags |= NodeFlags.Listed;
            folder.AddUp(size, alloc, files, folders, mtime);
            Interlocked.Add(ref _progress.Files, files);
            Interlocked.Add(ref _progress.Folders, folders);
            Interlocked.Add(ref _progress.Bytes, size);
        }

        /// <summary>A file's ID seen before means it's another name (hard link) of a file already counted.</summary>
        private bool SeenBefore(long fileId)
        {
            var set = _seenIds[(int)((ulong)fileId % (ulong)_seenIds.Length)];
            lock (set) return !set.Add(fileId);
        }

        private static bool IsFolderLink(uint tag) => tag == Native.IO_REPARSE_TAG_MOUNT_POINT || tag == Native.IO_REPARSE_TAG_SYMLINK;

        /// <summary>
        /// Lists a folder with GetFileInformationByHandleEx, which gives each file's size, its space on the drive and its ID
        /// in one pass. Returns null when the drive doesn't support it.
        /// </summary>
        private List<Node> ListWin32(string path)
        {
            using (var handle = Native.CreateFile(Paths.Long(path), Native.FILE_LIST_DIRECTORY, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE | Native.FILE_SHARE_DELETE,
                       IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero))
            {
                if (handle.IsInvalid)
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 5) throw new UnauthorizedAccessException("Access is denied.");
                    throw new IOException(new System.ComponentModel.Win32Exception(error).Message);
                }
                var list = new List<Node>();
                var buffer = new byte[64 * 1024];
                while (true)
                {
                    if (!Native.GetFileInformationByHandleEx(handle, Native.FileIdBothDirectoryInfo, buffer, buffer.Length))
                    {
                        var error = Marshal.GetLastWin32Error();
                        if (error == Native.ERROR_NO_MORE_FILES) break;
                        if (error == 87 || error == 50 || error == 1) return null;
                        if (error == 5) throw new UnauthorizedAccessException("Access is denied.");
                        throw new IOException(new System.ComponentModel.Win32Exception(error).Message);
                    }
                    var offset = 0;
                    while (true)
                    {
                        var next = BitConverter.ToInt32(buffer, offset);
                        var nameLength = BitConverter.ToInt32(buffer, offset + 60);
                        var name = Encoding.Unicode.GetString(buffer, offset + 104, nameLength);
                        if (name != "." && name != "..")
                        {
                            var attributes = (FileAttributes)BitConverter.ToUInt32(buffer, offset + 56);
                            var node = new Node
                            {
                                Name = name,
                                Attributes = attributes,
                                Mtime = Ticks(BitConverter.ToInt64(buffer, offset + 24)),
                                Atime = Ticks(BitConverter.ToInt64(buffer, offset + 16))
                            };
                            if ((attributes & FileAttributes.Directory) != 0)
                            {
                                node.Children = new List<Node>();
                                if ((attributes & FileAttributes.ReparsePoint) != 0 && IsFolderLink(BitConverter.ToUInt32(buffer, offset + 64)))
                                    node.Flags |= NodeFlags.Link;
                            }
                            else
                            {
                                node.Size = BitConverter.ToInt64(buffer, offset + 40);
                                node.Alloc = BitConverter.ToInt64(buffer, offset + 48);
                                var id = BitConverter.ToInt64(buffer, offset + 96);
                                if (_dedupeLinks && id != 0 && node.Alloc > 0 && SeenBefore(id))
                                {
                                    node.Alloc = 0;
                                    node.Flags |= NodeFlags.HardLink;
                                }
                            }
                            list.Add(node);
                        }
                        if (next == 0) break;
                        offset += next;
                    }
                }
                return list;
            }
        }

        private List<Node> ListManaged(string path)
        {
            var list = new List<Node>();
            foreach (var info in new DirectoryInfo(Paths.Long(path)).EnumerateFileSystemInfos())
            {
                var node = new Node { Name = info.Name, Attributes = info.Attributes, Mtime = info.LastWriteTimeUtc.Ticks, Atime = info.LastAccessTimeUtc.Ticks };
                if ((info.Attributes & FileAttributes.Directory) != 0)
                {
                    node.Children = new List<Node>();
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) node.Flags |= NodeFlags.Link;
                }
                else
                {
                    node.Size = ((FileInfo)info).Length;
                    node.Alloc = Allocated(info.FullName, node.Size, info.Attributes);
                }
                list.Add(node);
            }
            return list;
        }

        private const FileAttributes Cloud = (FileAttributes)0x400000 | (FileAttributes)0x40000 | FileAttributes.Offline;

        /// <summary>Space on the drive when the listing doesn't say: whole clusters, or what Windows reports for compressed and sparse files.</summary>
        private long Allocated(string path, long size, FileAttributes attributes)
        {
            if ((attributes & Cloud) != 0) return 0;
            if (Paths.IsWindows && (attributes & (FileAttributes.Compressed | FileAttributes.SparseFile)) != 0)
            {
                var low = Native.GetCompressedFileSize(path, out var high);
                if (low != 0xFFFFFFFF || Marshal.GetLastWin32Error() == 0) return ((long)high << 32) | low;
            }
            return size == 0 ? 0 : (size + _cluster - 1) / _cluster * _cluster;
        }

        public static long ClusterSize(string path)
        {
            if (Paths.IsWindows)
            {
                try
                {
                    var root = Paths.IsUnc(path) ? path.TrimEnd('\\') + "\\" : Path.GetPathRoot(path);
                    if (Native.GetDiskFreeSpace(root, out var spc, out var bps, out _, out _) && spc * bps > 0) return spc * bps;
                }
                catch { /* use the usual size */ }
            }
            return 4096;
        }

        private static long Ticks(long fileTime)
        {
            try { return fileTime > 0 ? DateTime.FromFileTimeUtc(fileTime).Ticks : 0; }
            catch { return 0; }
        }
    }
}
