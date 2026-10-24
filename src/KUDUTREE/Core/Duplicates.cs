using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace Kudutree.Core
{
    /// <summary>Files with exactly the same contents.</summary>
    public sealed class DuplicateGroup
    {
        public long Size;
        public List<Node> Files = new List<Node>();

        /// <summary>Space that would be freed by keeping just one copy.</summary>
        public long Wasted => Size * (Files.Count - 1);
    }

    public sealed class DuplicateProgress
    {
        public volatile string Phase = "Looking for files of the same size";
        public long Done;
        public long Total;
        public long BytesRead;
        public int Unreadable;
    }

    /// <summary>
    /// Finds files with the same contents: first files of exactly the same size, then the same first 64 KB, then the same
    /// whole contents (SHA-256). Hard links (the same file under two names) and online-only OneDrive files (reading them
    /// would download them) are left out. Files that can't be read are skipped and counted.
    /// </summary>
    public static class Duplicates
    {
        private const int Head = 64 * 1024;
        private const FileAttributes Cloud = (FileAttributes)0x400000 | (FileAttributes)0x40000 | FileAttributes.Offline;

        public static List<DuplicateGroup> Find(Node root, long minSize, DuplicateProgress progress = null, CancellationToken token = default)
        {
            progress = progress ?? new DuplicateProgress();
            minSize = Math.Max(1, minSize);
            var bySize = Queries.Files(root)
                .Where(f => f.Size >= minSize && !f.Has(NodeFlags.HardLink) && (f.Attributes & Cloud) == 0)
                .GroupBy(f => f.Size)
                .Where(g => g.Count() > 1)
                .Select(g => g.ToList())
                .ToList();

            progress.Phase = "Comparing the start of each file";
            progress.Total = bySize.Sum(g => g.Count);
            progress.Done = 0;
            var candidates = new List<List<Node>>();
            foreach (var group in bySize)
            {
                token.ThrowIfCancellationRequested();
                foreach (var same in Split(group, f => Hash(f, Head, progress, token), progress))
                    candidates.Add(same);
            }

            progress.Phase = "Comparing whole files";
            progress.Total = candidates.Where(g => g[0].Size > Head).Sum(g => g.Count);
            progress.Done = 0;
            var result = new List<DuplicateGroup>();
            foreach (var group in candidates)
            {
                token.ThrowIfCancellationRequested();
                if (group[0].Size <= Head)
                {
                    result.Add(new DuplicateGroup { Size = group[0].Size, Files = group });
                    continue;
                }
                foreach (var same in Split(group, f => Hash(f, long.MaxValue, progress, token), progress))
                    result.Add(new DuplicateGroup { Size = same[0].Size, Files = same });
            }
            foreach (var g in result) g.Files = g.Files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
            return result.OrderByDescending(g => g.Wasted).ThenBy(g => g.Files[0].Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Splits files into groups with the same hash; groups of one, and unreadable files, are dropped.</summary>
        private static IEnumerable<List<Node>> Split(List<Node> files, Func<Node, string> hash, DuplicateProgress progress)
        {
            var groups = new Dictionary<string, List<Node>>();
            foreach (var f in files)
            {
                var h = hash(f);
                Interlocked.Increment(ref progress.Done);
                if (h == null) continue;
                if (!groups.TryGetValue(h, out var list)) groups[h] = list = new List<Node>();
                list.Add(f);
            }
            return groups.Values.Where(g => g.Count > 1);
        }

        // On the .NET Framework, SHA256.Create() gives the slow managed version; Windows' own is several times faster.
        private static HashAlgorithm NewHash()
        {
#if NETFRAMEWORK
            return new SHA256Cng();
#else
            return SHA256.Create();
#endif
        }

        private static string Hash(Node file, long limit, DuplicateProgress progress, CancellationToken token)
        {
            try
            {
                using (var sha = NewHash())
                using (var stream = new FileStream(Paths.Long(file.Path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan))
                {
                    var buffer = new byte[1024 * 1024];
                    long read = 0;
                    while (read < limit)
                    {
                        token.ThrowIfCancellationRequested();
                        var n = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, limit - read));
                        if (n <= 0) break;
                        sha.TransformBlock(buffer, 0, n, null, 0);
                        read += n;
                        Interlocked.Add(ref progress.BytesRead, n);
                    }
                    sha.TransformFinalBlock(buffer, 0, 0);
                    // The length is part of the key, so a file that changed size since the scan can't match by accident.
                    return stream.Length + ":" + Convert.ToBase64String(sha.Hash);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                Interlocked.Increment(ref progress.Unreadable);
                return null;
            }
        }
    }
}
