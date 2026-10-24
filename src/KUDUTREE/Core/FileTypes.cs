using System;
using System.Collections.Generic;
using System.Linq;

namespace Kudutree.Core
{
    /// <summary>All files of one extension in a scan.</summary>
    public sealed class FileType
    {
        public int Index;
        /// <summary>".mp4", or "" for files without an extension.</summary>
        public string Extension;
        public string Group;
        public long Size;
        public long Alloc;
        public int Count;
        /// <summary>Palette colour (0–11) for the biggest types; -1 for the rest, which are drawn grey.</summary>
        public int Color = -1;

        public long Measure(bool onDisk) => onDisk ? Alloc : Size;
        public string Title => Extension.Length == 0 ? "(no extension)" : Extension;
    }

    /// <summary>The file types of a scan, with their totals and the colours used for them in the treemap.</summary>
    public sealed class FileTypes
    {
        public const int Colors = 12;

        public List<FileType> All { get; } = new List<FileType>();
        private readonly Dictionary<string, FileType> _byExtension = new Dictionary<string, FileType>(StringComparer.Ordinal);

        public FileType Get(int index) => index >= 0 && index < All.Count ? All[index] : null;

        public FileType For(string extension)
        {
            if (!_byExtension.TryGetValue(extension, out var t))
            {
                t = new FileType { Index = All.Count, Extension = extension, Group = GroupOf(extension) };
                All.Add(t);
                _byExtension[extension] = t;
            }
            return t;
        }

        /// <summary>Counts every file under <paramref name="root"/> and colours the biggest types (by space on disk).</summary>
        public static FileTypes Count(Node root)
        {
            var types = new FileTypes();
            var stack = new Stack<Node>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                if (n.IsDir)
                {
                    foreach (var c in n.Children) stack.Push(c);
                    continue;
                }
                if (n.Has(NodeFlags.Deleted)) continue;
                var t = types.For(n.Extension);
                n.Type = t.Index;
                t.Size += n.Size;
                t.Alloc += n.Alloc;
                t.Count++;
            }
            var color = 0;
            foreach (var t in types.All.OrderByDescending(t => t.Alloc).ThenByDescending(t => t.Size))
            {
                if (color >= Colors - 1 || (t.Alloc == 0 && t.Size == 0)) break;
                t.Color = color++;
            }
            return types;
        }

        /// <summary>Adds the files of a folder that was scanned again (new extensions are drawn grey).</summary>
        public void Add(Node node)
        {
            foreach (var n in Queries.Files(node))
            {
                var t = For(n.Extension);
                n.Type = t.Index;
                t.Size += n.Size;
                t.Alloc += n.Alloc;
                t.Count++;
            }
        }

        /// <summary>Takes a deleted file (or everything in a deleted folder) off the totals.</summary>
        public void Subtract(Node node)
        {
            var stack = new Stack<Node>();
            stack.Push(node);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                if (n.IsDir)
                {
                    foreach (var c in n.Children) stack.Push(c);
                    continue;
                }
                var t = Get(n.Type);
                if (t == null) continue;
                t.Size -= n.Size;
                t.Alloc -= n.Alloc;
                t.Count--;
            }
        }

        private static readonly Dictionary<string, string> Groups = BuildGroups();

        private static Dictionary<string, string> BuildGroups()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            void Add(string group, string extensions)
            {
                foreach (var e in extensions.Split(' ')) map["." + e] = group;
            }
            Add("Video", "mp4 mkv mov avi wmv m4v webm mpg mpeg ts mts m2ts 3gp flv vob");
            Add("Pictures", "jpg jpeg png gif bmp tif tiff heic heif webp raw cr2 cr3 nef arw dng orf rw2 psd svg ico");
            Add("Music", "mp3 wav flac m4a aac ogg opus wma aiff mid");
            Add("Documents", "pdf doc docx xls xlsx ppt pptx odt ods odp rtf txt md csv epub one pst ost msg eml");
            Add("Archives", "zip 7z rar gz tgz tar bz2 xz cab iso img vhd vhdx wim esd");
            Add("Programs", "exe dll msi msix appx sys drv ocx cpl mui");
            Add("Code", "cs js ts py java c cpp h hpp go rs rb php html css json xml yml yaml sql sh ps1 bat cmd");
            Add("Data", "db sqlite mdb accdb dat log bak tmp cache pak bin");
            return map;
        }

        public static string GroupOf(string extension) =>
            extension.Length > 0 && Groups.TryGetValue(extension, out var g) ? g : "Other";
    }
}
