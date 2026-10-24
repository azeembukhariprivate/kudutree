using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace Kudutree.Core
{
    public enum FindKind { FilesAndFolders, Files, Folders }

    public sealed class SearchQuery
    {
        /// <summary>Part of a name ("invoice"), or a pattern with * and ? ("*.iso", "IMG_????.jpg"). Empty matches everything.</summary>
        public string Text { get; set; } = string.Empty;
        public long MinSize { get; set; }
        /// <summary>Only items last changed before this (local time), e.g. "not changed for a year".</summary>
        public DateTime? ChangedBefore { get; set; }
        /// <summary>Only items changed after this.</summary>
        public DateTime? ChangedAfter { get; set; }
        public FindKind Kind { get; set; } = FindKind.FilesAndFolders;
        public bool OnDisk { get; set; } = true;
        public int Limit { get; set; } = 5000;
    }

    public static class Queries
    {
        /// <summary>The biggest files under a folder, biggest first.</summary>
        public static List<Node> Largest(Node root, int count, bool onDisk)
        {
            // A min-heap of the best so far: one pass, no sorting of every file.
            var heap = new List<Node>(count + 1);
            long Key(Node n) => n.Measure(onDisk);
            void SiftUp(int i)
            {
                while (i > 0)
                {
                    var parent = (i - 1) / 2;
                    if (Key(heap[parent]) <= Key(heap[i])) break;
                    (heap[parent], heap[i]) = (heap[i], heap[parent]);
                    i = parent;
                }
            }
            void SiftDown(int i)
            {
                while (true)
                {
                    int l = i * 2 + 1, r = l + 1, m = i;
                    if (l < heap.Count && Key(heap[l]) < Key(heap[m])) m = l;
                    if (r < heap.Count && Key(heap[r]) < Key(heap[m])) m = r;
                    if (m == i) return;
                    (heap[m], heap[i]) = (heap[i], heap[m]);
                    i = m;
                }
            }
            foreach (var file in Files(root))
            {
                if (heap.Count < count)
                {
                    heap.Add(file);
                    SiftUp(heap.Count - 1);
                }
                else if (count > 0 && Key(file) > Key(heap[0]))
                {
                    heap[0] = file;
                    SiftDown(0);
                }
            }
            return heap.OrderByDescending(Key).ToList();
        }

        public static IEnumerable<Node> Files(Node root)
        {
            var stack = new Stack<Node>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                if (n.Has(NodeFlags.Deleted)) continue;
                if (!n.IsDir)
                {
                    yield return n;
                    continue;
                }
                if (!n.Has(NodeFlags.Listed)) continue;
                for (var i = n.Children.Count - 1; i >= 0; i--) stack.Push(n.Children[i]);
            }
        }

        public static IEnumerable<Node> All(Node root)
        {
            var stack = new Stack<Node>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                if (n.Has(NodeFlags.Deleted)) continue;
                yield return n;
                if (n.IsDir && n.Has(NodeFlags.Listed))
                    for (var i = n.Children.Count - 1; i >= 0; i--) stack.Push(n.Children[i]);
            }
        }

        /// <summary>Finds matching files and folders under a folder, biggest first. <paramref name="total"/> is how many matched in all.</summary>
        public static List<Node> Search(Node root, SearchQuery q, out int total, CancellationToken token = default)
        {
            var match = Matcher(q.Text);
            var found = new List<Node>();
            total = 0;
            var checkedCount = 0;
            foreach (var n in All(root))
            {
                if (n == root) continue;
                if (++checkedCount % 10000 == 0) token.ThrowIfCancellationRequested();
                if (q.Kind == FindKind.Files && n.IsDir || q.Kind == FindKind.Folders && !n.IsDir) continue;
                if (n.Measure(q.OnDisk) < q.MinSize) continue;
                var modified = n.Modified;
                if (q.ChangedBefore != null && (modified == DateTime.MinValue || modified >= q.ChangedBefore.Value)) continue;
                if (q.ChangedAfter != null && modified < q.ChangedAfter.Value) continue;
                if (!match(n.Name)) continue;
                total++;
                found.Add(n);
            }
            return found.OrderByDescending(n => n.Measure(q.OnDisk)).Take(q.Limit).ToList();
        }

        public static Func<string, bool> Matcher(string text)
        {
            text = (text ?? string.Empty).Trim();
            if (text.Length == 0) return _ => true;
            if (text.IndexOfAny(new[] { '*', '?' }) < 0) return name => name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
            var patterns = text.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => new Regex("^" + Regex.Escape(p.Trim()).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                .ToArray();
            return name => patterns.Any(r => r.IsMatch(name));
        }

        /// <summary>
        /// Takes a deleted file or folder out of the scan: its parent folders lose its size and counts, and it's no longer
        /// listed anywhere. (The scan is updated without scanning again.)
        /// </summary>
        public static void Remove(ScanResult scan, Node node)
        {
            if (node.Parent == null) return;
            scan.Types.Subtract(node);
            var files = node.IsDir ? node.Files : 1;
            var folders = node.IsDir ? node.Folders + 1 : 0;
            for (var p = node.Parent; p != null; p = p.Parent)
            {
                p.Size -= node.Size;
                p.Alloc -= node.Alloc;
                p.Files -= files;
                p.Folders -= folders;
            }
            node.Parent.Children.Remove(node);
            node.Flags |= NodeFlags.Deleted;
        }

        /// <summary>
        /// Puts a fresh scan of a folder in place of the old one (after "Scan this folder again", or a delete that only
        /// partly worked): the folders above it get the difference.
        /// </summary>
        public static void Replace(ScanResult scan, Node old, Node fresh)
        {
            scan.Types.Subtract(old);
            long size = fresh.Size - old.Size, alloc = fresh.Alloc - old.Alloc;
            int files = fresh.Files - old.Files, folders = fresh.Folders - old.Folders;
            for (var p = old.Parent; p != null; p = p.Parent)
            {
                p.Size += size;
                p.Alloc += alloc;
                p.Files += files;
                p.Folders += folders;
            }
            old.Children = fresh.Children;
            foreach (var c in old.Children) c.Parent = old;
            old.Size = fresh.Size;
            old.Alloc = fresh.Alloc;
            old.Files = fresh.Files;
            old.Folders = fresh.Folders;
            old.Mtime = fresh.Mtime;
            old.Flags = fresh.Flags;
            scan.Types.Add(old);
        }

        /// <summary>Finds the node for a path inside a scan, or null.</summary>
        public static Node Find(Node root, string path)
        {
            var rootPath = root.Name.TrimEnd('\\', '/');
            path = (path ?? string.Empty).TrimEnd('\\', '/');
            if (path.Equals(rootPath, StringComparison.OrdinalIgnoreCase)) return root;
            if (!path.StartsWith(rootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
            var node = root;
            foreach (var part in path.Substring(rootPath.Length + 1).Split(Path.DirectorySeparatorChar))
            {
                node = node.IsDir ? node.Children.FirstOrDefault(c => string.Equals(c.Name, part, StringComparison.OrdinalIgnoreCase)) : null;
                if (node == null) return null;
            }
            return node;
        }
    }
}
