using System;
using System.Collections.Generic;
using System.Linq;

namespace Kudutree.Core
{
    public enum ChangeKind { Grew, Shrank, New, Removed }

    /// <summary>A file or folder whose size is different from a saved scan.</summary>
    public sealed class Change
    {
        public string Path;
        public bool IsDir;
        /// <summary>The item in the current scan (null when it's gone).</summary>
        public Node Now;
        /// <summary>The item in the saved scan (null when it's new).</summary>
        public Node Then;
        public long Before;
        public long After;

        public long Delta => After - Before;
        public ChangeKind Kind => Then == null ? ChangeKind.New : Now == null ? ChangeKind.Removed : Delta >= 0 ? ChangeKind.Grew : ChangeKind.Shrank;
    }

    /// <summary>What changed between a saved scan and the current one.</summary>
    public sealed class ComparisonResult
    {
        public ScanResult Then;
        public List<Change> Changes = new List<Change>();
        public long Before;
        public long After;
        public long Delta => After - Before;
    }

    public static class Comparison
    {
        /// <summary>
        /// Compares two scans of the same place, item by item (names match regardless of capitalisation). New and removed
        /// folders are listed as one item each; folders that are in both are compared inside too. Biggest change first.
        /// </summary>
        public static ComparisonResult Compare(ScanResult now, ScanResult then, bool onDisk)
        {
            var result = new ComparisonResult { Then = then, Before = then.Root.Measure(onDisk), After = now.Root.Measure(onDisk) };
            var stack = new Stack<(Node Now, Node Then)>();
            stack.Push((now.Root, then.Root));
            while (stack.Count > 0)
            {
                var (a, b) = stack.Pop();
                var old = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in b.Children) if (!old.ContainsKey(c.Name)) old[c.Name] = c;
                foreach (var c in a.Children)
                {
                    if (c.Has(NodeFlags.Deleted)) continue;
                    if (old.TryGetValue(c.Name, out var o))
                    {
                        old.Remove(c.Name);
                        if (c.IsDir != o.IsDir)
                        {
                            result.Changes.Add(new Change { Path = c.Path, IsDir = o.IsDir, Then = o, Before = o.Measure(onDisk) });
                            result.Changes.Add(new Change { Path = c.Path, IsDir = c.IsDir, Now = c, After = c.Measure(onDisk) });
                            continue;
                        }
                        if (c.Measure(onDisk) != o.Measure(onDisk))
                            result.Changes.Add(new Change { Path = c.Path, IsDir = c.IsDir, Now = c, Then = o, Before = o.Measure(onDisk), After = c.Measure(onDisk) });
                        if (c.IsDir && c.Has(NodeFlags.Listed) && o.Has(NodeFlags.Listed)) stack.Push((c, o));
                    }
                    else result.Changes.Add(new Change { Path = c.Path, IsDir = c.IsDir, Now = c, After = c.Measure(onDisk) });
                }
                foreach (var o in old.Values)
                    result.Changes.Add(new Change { Path = PathIn(a, o.Name), IsDir = o.IsDir, Then = o, Before = o.Measure(onDisk) });
            }
            result.Changes = result.Changes.Where(c => c.Delta != 0 || c.Kind == ChangeKind.New || c.Kind == ChangeKind.Removed)
                .OrderByDescending(c => Math.Abs(c.Delta)).ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ToList();
            return result;
        }

        private static string PathIn(Node folder, string name)
        {
            var p = folder.Path;
            return p.EndsWith("\\") || p.EndsWith("/") ? p + name : p + System.IO.Path.DirectorySeparatorChar + name;
        }
    }
}
