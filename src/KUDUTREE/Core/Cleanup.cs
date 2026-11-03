using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace Kudutree.Core
{
    /// <summary>What happens to a kind of file when it's cleaned.</summary>
    public enum CleanWay
    {
        /// <summary>Your own files: moved to the Recycle Bin.</summary>
        RecycleBin,

        /// <summary>Windows-wide leftovers: deleted permanently (after asking), with administrator rights.</summary>
        Permanently,

        /// <summary>Too tangled to delete file by file: only measured, and Windows' own cleanup is offered.</summary>
        WindowsCleanup
    }

    /// <summary>One kind of file that's safe to clean, and the exact folders it lives in.</summary>
    public sealed class CleanSpot
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string About { get; set; }
        public CleanWay Way { get; set; }
        /// <summary>The folders whose contents are cleaned (never the folders themselves).</summary>
        public List<string> Folders { get; set; } = new List<string>();
        /// <summary>Single files cleaned as they are (like a memory dump).</summary>
        public List<string> SingleFiles { get; set; } = new List<string>();
        /// <summary>Only items with nothing changed for this long (temporary files a program may still be using are left).</summary>
        public TimeSpan MinAge { get; set; }
        /// <summary>Only files whose names match this, like "thumbcache_*.db" (null: everything).</summary>
        public string Pattern { get; set; }
        public bool NeedsAdmin => Way != CleanWay.RecycleBin;
    }

    /// <summary>A file or folder that will be cleaned as one: a top-level item in one of a spot's folders.</summary>
    public sealed class CleanItem
    {
        public string Path { get; set; }
        public bool IsDir { get; set; }
        public long Bytes { get; set; }
        public int Files { get; set; }
        public DateTime Newest { get; set; }
    }

    /// <summary>What a spot holds now.</summary>
    public sealed class Measured
    {
        public CleanSpot Spot { get; set; }
        public List<CleanItem> Items { get; set; } = new List<CleanItem>();
        public long Bytes => Items.Sum(i => i.Bytes);
        public int Files => Items.Sum(i => i.Files);
        /// <summary>Items left because they changed too recently.</summary>
        public int TooNew { get; set; }
    }

    /// <summary>What cleaning did.</summary>
    public sealed class CleanResult
    {
        public long Freed { get; set; }
        public int FilesCleaned { get; set; }
        /// <summary>Items that couldn't be cleaned (usually because a program has them open).</summary>
        public List<string> Skipped { get; } = new List<string>();
    }

    /// <summary>The folders this PC uses (passed in, so the tests can use made-up ones).</summary>
    public sealed class Places
    {
        public string Temp { get; set; }
        public string LocalAppData { get; set; }
        public string Windows { get; set; }
        public string ProgramData { get; set; }
        public string SystemDrive { get; set; }

        public static Places ThisPc()
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            return new Places
            {
                Temp = Path.GetTempPath(),
                LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Windows = windows,
                ProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                SystemDrive = string.IsNullOrEmpty(windows) ? string.Empty : Path.GetPathRoot(windows)
            };
        }
    }

    /// <summary>
    /// "Safe to clean": kinds of files that can go without harm, in exact, known folders only. Nothing outside those
    /// folders is ever touched, the folders themselves are kept, and links inside them are never followed.
    /// </summary>
    public static class Cleanup
    {
        private static readonly string[] CacheFolders = { "Cache", "Code Cache", "GPUCache" };

        public static List<CleanSpot> Spots(Places p)
        {
            var spots = new List<CleanSpot>();
            string In(params string[] parts) => Path.Combine(parts);

            // ---- your own files (to the Recycle Bin)
            spots.Add(new CleanSpot
            {
                Id = "temp", Name = "Your temporary files", Way = CleanWay.RecycleBin, MinAge = TimeSpan.FromDays(1),
                About = "Files programs make while they work and usually forget to remove. Only ones untouched for a day are cleaned, so nothing a program is using now goes.",
                Folders = { p.Temp }
            });
            spots.Add(Browser("edge", "Microsoft Edge", ChromiumProfiles(In(p.LocalAppData, "Microsoft", "Edge", "User Data"))));
            spots.Add(Browser("chrome", "Google Chrome", ChromiumProfiles(In(p.LocalAppData, "Google", "Chrome", "User Data"))));
            spots.Add(Browser("brave", "Brave", ChromiumProfiles(In(p.LocalAppData, "BraveSoftware", "Brave-Browser", "User Data"))));
            spots.Add(Browser("firefox", "Firefox", Subfolders(In(p.LocalAppData, "Mozilla", "Firefox", "Profiles")).Select(f => In(f, "cache2")).ToList()));
            spots.Add(Browser("kudubrowse", "KUDUBROWSE",
                Subfolders(In(p.LocalAppData, "KUDUBROWSE", "Profiles")).SelectMany(f => ChromiumProfiles(In(f, "EBWebView"))).ToList()));
            spots.Add(new CleanSpot
            {
                Id = "thumbnails", Name = "Picture previews", Way = CleanWay.RecycleBin, Pattern = "thumbcache_*.db",
                About = "Small previews File Explorer keeps of your pictures and videos. They're made again when folders are opened. Some may be in use and stay.",
                Folders = { In(p.LocalAppData, "Microsoft", "Windows", "Explorer") }
            });
            spots.Add(new CleanSpot
            {
                Id = "reports", Name = "Your error reports and crash files", Way = CleanWay.RecycleBin,
                About = "Reports kept after programs stopped working, and their crash files. Windows has already sent what it needed.",
                Folders = { In(p.LocalAppData, "Microsoft", "Windows", "WER", "ReportArchive"), In(p.LocalAppData, "Microsoft", "Windows", "WER", "ReportQueue"), In(p.LocalAppData, "CrashDumps") }
            });

            // ---- Windows-wide leftovers (permanently, administrator rights)
            spots.Add(new CleanSpot
            {
                Id = "updates", Name = "Downloaded Windows updates", Way = CleanWay.Permanently,
                About = "Update files Windows downloaded and has already installed. Windows downloads them again if it ever needs them.",
                Folders = { In(p.Windows, "SoftwareDistribution", "Download") }
            });
            spots.Add(new CleanSpot
            {
                Id = "wintemp", Name = "Windows' temporary files", Way = CleanWay.Permanently, MinAge = TimeSpan.FromDays(1),
                About = "Windows' own temporary files. Only ones untouched for a day are cleaned.",
                Folders = { In(p.Windows, "Temp") }
            });
            spots.Add(new CleanSpot
            {
                Id = "dumps", Name = "Crash dumps and error reports for everyone", Way = CleanWay.Permanently,
                About = "Copies of memory Windows saved when the PC or a program crashed, and error reports for all accounts. Only useful to someone fixing that crash.",
                Folders = { In(p.Windows, "Minidump"), In(p.Windows, "LiveKernelReports"), In(p.ProgramData, "Microsoft", "Windows", "WER", "ReportArchive"), In(p.ProgramData, "Microsoft", "Windows", "WER", "ReportQueue") },
                SingleFiles = { In(p.Windows, "MEMORY.DMP") }
            });
            spots.Add(new CleanSpot
            {
                Id = "delivery", Name = "Update sharing files", Way = CleanWay.Permanently,
                About = "Pieces of updates Windows keeps to share with other PCs. They're fetched again if needed.",
                Folders = { In(p.Windows, "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache") }
            });

            // ---- only Windows' own cleanup can remove these
            spots.Add(new CleanSpot
            {
                Id = "windowsold", Name = "Your previous Windows (Windows.old)", Way = CleanWay.WindowsCleanup,
                About = "Kept after a big Windows update, to go back to the earlier version. Windows' cleanup removes it safely (it's usually removed by itself after 10 days).",
                Folders = { In(p.SystemDrive ?? string.Empty, "Windows.old") }
            });
            spots.Add(new CleanSpot
            {
                Id = "components", Name = "Old system parts from updates", Way = CleanWay.WindowsCleanup,
                About = "Earlier copies of Windows' own parts that updates replaced. They're shared with the running Windows, so only Windows' cleanup can tell which can go.",
            });
            return spots;
        }

        private static CleanSpot Browser(string id, string name, List<string> folders) => new CleanSpot
        {
            Id = id, Name = name + " cache", Way = CleanWay.RecycleBin, Folders = folders,
            About = "Copies of web pages and pictures " + name + " keeps to load sites faster. They're fetched again when needed; your passwords, history and logins aren't touched. While " + name + " is open some of it stays."
        };

        /// <summary>The cache folders of every profile in a Chromium browser's data folder ("Default", "Profile 1"...).</summary>
        private static List<string> ChromiumProfiles(string userData) =>
            Subfolders(userData).SelectMany(profile => CacheFolders.Select(c => Path.Combine(profile, c))).ToList();

        private static IEnumerable<string> Subfolders(string folder)
        {
            try
            {
                return Directory.Exists(folder)
                    ? new DirectoryInfo(folder).EnumerateDirectories().Where(d => (d.Attributes & FileAttributes.ReparsePoint) == 0).Select(d => d.FullName).ToList()
                    : new List<string>();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return new List<string>();
            }
        }

        // ================================================================ measuring

        /// <summary>Lists what a spot holds now, as items to clean (the top-level files and folders in its folders).</summary>
        public static Measured Measure(CleanSpot spot, DateTime now, CancellationToken cancel = default)
        {
            var m = new Measured { Spot = spot };
            var match = spot.Pattern == null ? null : new Regex("^" + Regex.Escape(spot.Pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase);
            foreach (var folder in spot.Folders.Where(f => !string.IsNullOrEmpty(f)))
            {
                cancel.ThrowIfCancellationRequested();
                DirectoryInfo dir;
                try
                {
                    dir = new DirectoryInfo(folder);
                    if (!dir.Exists || (dir.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException) { continue; }
                IEnumerable<FileSystemInfo> entries;
                try { entries = dir.EnumerateFileSystemInfos().ToList(); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { continue; }
                foreach (var e in entries)
                {
                    cancel.ThrowIfCancellationRequested();
                    // Links (junctions, symbolic links) are never followed or removed.
                    if ((e.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (match != null && (e is DirectoryInfo || !match.IsMatch(e.Name))) continue;
                    var item = Describe(e, cancel);
                    if (item == null) continue;
                    if (spot.MinAge > TimeSpan.Zero && now - item.Newest < spot.MinAge)
                    {
                        m.TooNew++;
                        continue;
                    }
                    m.Items.Add(item);
                }
            }
            foreach (var file in spot.SingleFiles.Where(f => !string.IsNullOrEmpty(f)))
            {
                try
                {
                    var f = new FileInfo(file);
                    if (f.Exists && (f.Attributes & FileAttributes.ReparsePoint) == 0)
                        m.Items.Add(new CleanItem { Path = f.FullName, Bytes = f.Length, Files = 1, Newest = f.LastWriteTime });
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
            return m;
        }

        private static CleanItem Describe(FileSystemInfo e, CancellationToken cancel)
        {
            try
            {
                if (e is FileInfo f) return new CleanItem { Path = f.FullName, Bytes = f.Length, Files = 1, Newest = Latest(f) };
                var item = new CleanItem { Path = e.FullName, IsDir = true, Newest = Latest(e) };
                var stack = new Stack<DirectoryInfo>();
                stack.Push((DirectoryInfo)e);
                while (stack.Count > 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    var d = stack.Pop();
                    foreach (var child in d.EnumerateFileSystemInfos())
                    {
                        // A folder holding a link is left whole: removing it could reach through the link.
                        if ((child.Attributes & FileAttributes.ReparsePoint) != 0) return null;
                        var t = Latest(child);
                        if (t > item.Newest) item.Newest = t;
                        if (child is DirectoryInfo sub) stack.Push(sub);
                        else if (child is FileInfo cf)
                        {
                            item.Bytes += cf.Length;
                            item.Files++;
                        }
                    }
                }
                return item;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static DateTime Latest(FileSystemInfo i) => i.LastWriteTime > i.CreationTime ? i.LastWriteTime : i.CreationTime;

        // ================================================================ cleaning

        /// <summary>True when the path is strictly inside one of the spot's folders, or is one of its single files.</summary>
        public static bool Belongs(CleanSpot spot, string path)
        {
            string Full(string p) => Path.GetFullPath(p).TrimEnd('\\', '/');
            var full = Full(path);
            if (spot.SingleFiles.Any(f => !string.IsNullOrEmpty(f) && string.Equals(Full(f), full, StringComparison.OrdinalIgnoreCase))) return true;
            return spot.Folders.Any(f => !string.IsNullOrEmpty(f) && full.StartsWith(Full(f) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Cleans the measured items. <paramref name="recycle"/> moves a batch of paths to the Recycle Bin and says whether
        /// all of it worked; a batch that fails is tried one by one, so one file in use only keeps itself. Windows-wide
        /// spots are deleted permanently. <paramref name="progress"/> gets the bytes done.
        /// </summary>
        public static CleanResult Clean(IEnumerable<Measured> measured, Func<IList<string>, bool> recycle, Action<long> progress, CancellationToken cancel)
        {
            var result = new CleanResult();
            foreach (var m in measured)
            {
                if (m.Spot.Way == CleanWay.WindowsCleanup) continue;
                var items = m.Items.Where(i => Belongs(m.Spot, i.Path)).ToList();
                if (m.Spot.Way == CleanWay.RecycleBin)
                {
                    foreach (var batch in Batches(items, 200))
                    {
                        cancel.ThrowIfCancellationRequested();
                        if (recycle(batch.Select(i => i.Path).ToList()))
                        {
                            foreach (var i in batch) Done(result, i, progress);
                            continue;
                        }
                        foreach (var i in batch)
                        {
                            cancel.ThrowIfCancellationRequested();
                            if (!Exists(i)) Done(result, i, progress); // the batch got it before stopping
                            else if (recycle(new[] { i.Path }) && !Exists(i)) Done(result, i, progress);
                            else result.Skipped.Add(i.Path);
                        }
                    }
                }
                else
                {
                    foreach (var i in items)
                    {
                        cancel.ThrowIfCancellationRequested();
                        if (DeletePermanently(i)) Done(result, i, progress);
                        else result.Skipped.Add(i.Path);
                    }
                }
            }
            return result;
        }

        private static void Done(CleanResult r, CleanItem i, Action<long> progress)
        {
            r.Freed += i.Bytes;
            r.FilesCleaned += i.Files;
            progress?.Invoke(r.Freed);
        }

        private static bool Exists(CleanItem i) => i.IsDir ? Directory.Exists(i.Path) : File.Exists(i.Path);

        private static IEnumerable<List<CleanItem>> Batches(List<CleanItem> items, int size)
        {
            for (var i = 0; i < items.Count; i += size) yield return items.Skip(i).Take(size).ToList();
        }

        /// <summary>Deletes an item for good; a folder is emptied as far as it can be. True when it's all gone.</summary>
        public static bool DeletePermanently(CleanItem i)
        {
            try
            {
                if (!i.IsDir)
                {
                    var a = File.GetAttributes(i.Path);
                    if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(i.Path, a & ~FileAttributes.ReadOnly);
                    File.Delete(i.Path);
                    return true;
                }
                // Inside out, never through links.
                var ok = true;
                foreach (var d in new DirectoryInfo(i.Path).EnumerateDirectories())
                {
                    if ((d.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                    ok &= DeletePermanently(new CleanItem { Path = d.FullName, IsDir = true });
                }
                foreach (var f in new DirectoryInfo(i.Path).EnumerateFiles())
                {
                    try
                    {
                        if ((f.Attributes & FileAttributes.ReadOnly) != 0) f.Attributes &= ~FileAttributes.ReadOnly;
                        f.Delete();
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { ok = false; }
                }
                if (ok) Directory.Delete(i.Path, false);
                return ok;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}
