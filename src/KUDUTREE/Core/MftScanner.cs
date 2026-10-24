using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Kudutree.Core
{
    /// <summary>
    /// The fast scan: reads an NTFS drive's file index (the MFT) directly, in big sequential pieces, instead of asking
    /// Windows for every folder. A full drive with a million files takes seconds. Needs administrator rights.
    /// Sizes come straight from the index: the file size, and the space on the drive (compressed and sparse files
    /// count what they really use, small files kept inside the index count nothing, hard links count once, and a
    /// folder's own index and alternate data streams are included).
    /// </summary>
    public sealed class MftScanner
    {
        private const int Root = 5;
        private const byte InUse = 1, Dir = 2, HasName = 4, DosName = 8;

        private readonly IBlockDevice _device;
        private readonly NtfsBoot _boot;
        private List<DataRun> _runs;
        private long _mftSize;

        // Per record (index = record number).
        private long[] _size, _alloc, _mtime;
        private int[] _parent;
        private ushort[] _parentSeq, _seq;
        private uint[] _attr;
        private byte[] _flags;
        private string[] _name;
        private readonly List<(int Record, int Parent, ushort ParentSeq, string Name)> _links = new List<(int, int, ushort, string)>();

        private MftScanner(IBlockDevice device, NtfsBoot boot)
        {
            _device = device;
            _boot = boot;
        }

        public static bool IsNtfs(IBlockDevice device)
        {
            var sector = new byte[Math.Max(512, device.SectorSize)];
            device.Read(0, sector, 0, sector.Length);
            return NtfsBoot.IsNtfs(sector);
        }

        public static ScanResult Scan(IBlockDevice device, string rootName, ScanProgress progress = null, CancellationToken token = default)
        {
            progress = progress ?? new ScanProgress();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var started = DateTime.Now;
            var sector = new byte[Math.Max(512, device.SectorSize)];
            device.Read(0, sector, 0, sector.Length);
            if (NtfsBoot.IsBitLocker(sector)) throw new InvalidDataException("This drive is locked with BitLocker. Unlock it first.");
            var scanner = new MftScanner(device, NtfsBoot.Parse(sector));
            progress.Phase = "Reading the file index";
            scanner.LoadMft();
            scanner.ReadAll(progress, token);
            progress.Phase = "Building the folder tree";
            var root = scanner.Build(rootName, token);
            var result = new ScanResult
            {
                Root = root,
                Path = rootName,
                Method = "Fast scan (file index)",
                Fast = true,
                Started = started,
                ClusterSize = scanner._boot.ClusterSize
            };
            progress.Files = root.Files;
            progress.Folders = root.Folders;
            progress.Bytes = root.Size;
            result.Finish();
            result.Duration = clock.Elapsed;
            return result;
        }

        // ------------------------------------------------------------------ finding the MFT

        private int RecordSize => _boot.RecordSize;
        private int Cluster => _boot.ClusterSize;

        private void LoadMft()
        {
            FileRecord mft = null;
            foreach (var lcn in new[] { _boot.MftLcn, _boot.MftMirrLcn })
            {
                var buffer = new byte[RecordSize];
                try { _device.Read(lcn * Cluster, buffer, 0, RecordSize); }
                catch (IOException) { continue; }
                mft = FileRecord.Parse(buffer, 0, RecordSize, 0);
                if (mft?.Find(AttrType.Data)?.Runs != null) break;
                mft = null;
            }
            if (mft == null) throw new InvalidDataException("The drive's file index (MFT) can't be read.");

            var first = mft.Find(AttrType.Data);
            _runs = first.Runs;
            _mftSize = first.DataSize;

            // A very large MFT continues its list of pieces in other records (which lie in the part already known).
            var list = mft.Find(AttrType.AttributeList);
            if (list == null) return;
            var value = list.Value;
            if (list.NonResident)
            {
                value = new byte[list.DataSize];
                ReadRuns(list.Runs, 0, value, 0, value.Length);
            }
            var pieces = new List<NtfsAttribute> { first };
            for (var pos = 0; value != null && pos + 26 <= value.Length;)
            {
                var type = BitConverter.ToUInt32(value, pos);
                var length = BitConverter.ToUInt16(value, pos + 4);
                if (length < 26 || pos + length > value.Length) break;
                var segment = BitConverter.ToInt64(value, pos + 16) & 0x0000FFFFFFFFFFFF;
                if (type == (uint)AttrType.Data && value[pos + 6] == 0 && segment != 0)
                {
                    var buffer = new byte[RecordSize];
                    ReadRuns(_runs, segment * RecordSize, buffer, 0, RecordSize);
                    var ext = FileRecord.Parse(buffer, 0, RecordSize, segment);
                    if (ext != null) pieces.AddRange(ext.Attributes.Where(a => a.Type == AttrType.Data && a.Name.Length == 0 && a.NonResident && a.StartVcn > 0));
                }
                pos += length;
            }
            _runs = pieces.OrderBy(p => p.StartVcn).SelectMany(p => p.Runs).ToList();
        }

        private void ReadRuns(List<DataRun> runs, long position, byte[] buffer, int index, int count)
        {
            while (count > 0)
            {
                var vcn = position / Cluster;
                DataRun? found = null;
                foreach (var r in runs)
                    if (vcn >= r.Vcn && vcn < r.Vcn + r.Length)
                    {
                        found = r;
                        break;
                    }
                if (found == null) throw new IOException("Position outside the file index.");
                var run = found.Value;
                var inRun = position - run.Vcn * Cluster;
                var n = (int)Math.Min(count, run.Length * Cluster - inRun);
                if (run.IsSparse) Array.Clear(buffer, index, n);
                else _device.Read(run.Lcn * Cluster + inRun, buffer, index, n);
                position += n;
                index += n;
                count -= n;
            }
        }

        // ------------------------------------------------------------------ reading every record

        private void ReadAll(ScanProgress progress, CancellationToken token)
        {
            var total = (int)Math.Min(int.MaxValue - 1, _mftSize / RecordSize);
            progress.Total = total;
            _size = new long[total];
            _alloc = new long[total];
            _mtime = new long[total];
            _parent = new int[total];
            _parentSeq = new ushort[total];
            _seq = new ushort[total];
            _attr = new uint[total];
            _flags = new byte[total];
            _name = new string[total];

            var chunkRecords = Math.Max(1, (4 * 1024 * 1024) / RecordSize);
            var buffer = new byte[chunkRecords * RecordSize];
            for (var first = 0; first < total; first += chunkRecords)
            {
                token.ThrowIfCancellationRequested();
                var count = Math.Min(chunkRecords, total - first);
                try { ReadRuns(_runs, (long)first * RecordSize, buffer, 0, count * RecordSize); }
                catch (IOException)
                {
                    // A damaged piece: read what can be read record by record.
                    for (var i = 0; i < count; i++)
                    {
                        try { ReadRuns(_runs, (long)(first + i) * RecordSize, buffer, i * RecordSize, RecordSize); }
                        catch (IOException) { Array.Clear(buffer, i * RecordSize, RecordSize); }
                    }
                }
                for (var i = 0; i < count; i++) ParseRecord(buffer, i * RecordSize, first + i);
                progress.Done = first + count;
            }
        }

        private void ParseRecord(byte[] d, int o, int number)
        {
            if (BitConverter.ToUInt32(d, o) != 0x454C4946) return; // "FILE"
            if (!FileRecord.ApplyFixups(d, o, RecordSize)) return;
            var flags = BitConverter.ToUInt16(d, o + 0x16);
            if ((flags & 1) == 0) return;
            var baseRecord = BitConverter.ToInt64(d, o + 0x20) & 0x0000FFFFFFFFFFFF;
            var target = baseRecord != 0 ? (int)baseRecord : number;
            if (target < 0 || target >= _flags.Length) return;
            if (baseRecord == 0)
            {
                _flags[target] |= InUse;
                if ((flags & 2) != 0) _flags[target] |= Dir;
                _seq[target] = BitConverter.ToUInt16(d, o + 0x10);
            }

            int pos = BitConverter.ToUInt16(d, o + 0x14);
            var used = (int)Math.Min(BitConverter.ToUInt32(d, o + 0x18), (uint)RecordSize);
            while (pos + 16 <= used)
            {
                var p = o + pos;
                var type = BitConverter.ToUInt32(d, p);
                if (type == 0xFFFFFFFF || type == 0) break;
                var length = (int)BitConverter.ToUInt32(d, p + 4);
                if (length < 16 || pos + length > used) break;
                var nonResident = d[p + 8] != 0;
                var named = d[p + 9] != 0;

                switch (type)
                {
                    case 0x10 when !nonResident: // $STANDARD_INFORMATION
                    {
                        var v = p + BitConverter.ToUInt16(d, p + 20);
                        if (v + 36 <= p + length)
                        {
                            _mtime[target] = Ticks(BitConverter.ToInt64(d, v + 8));
                            _attr[target] = BitConverter.ToUInt32(d, v + 32);
                        }
                        break;
                    }
                    case 0x30 when !nonResident: // $FILE_NAME
                        FileName(d, p + BitConverter.ToUInt16(d, p + 20), p + length, target);
                        break;
                    case 0x80: // $DATA
                    case 0xA0: // $INDEX_ALLOCATION (a folder's own index)
                        if (nonResident)
                        {
                            if (length < 0x40) break;
                            var firstPiece = BitConverter.ToInt64(d, p + 16) == 0;
                            var attrFlags = BitConverter.ToUInt16(d, p + 12);
                            var runsOffset = BitConverter.ToUInt16(d, p + 32);
                            if (runsOffset < 0x40 || runsOffset >= length) break;
                            if ((attrFlags & 0x0001) != 0 && runsOffset >= 0x48)
                            {
                                // Compressed: NTFS keeps the total really used in the first piece.
                                if (firstPiece) _alloc[target] += BitConverter.ToInt64(d, p + 0x40);
                            }
                            else
                            {
                                // Everything else: the clusters each piece really has on the drive. Sparse runs take
                                // none, so sparse files, $UsnJrnl and $BadClus (whose sizes cover huge ranges) count
                                // only what they use.
                                _alloc[target] += UsedClusters(d, p + runsOffset, p + length) * Cluster;
                            }
                            if (firstPiece && type == 0x80 && !named) _size[target] = BitConverter.ToInt64(d, p + 48);
                        }
                        else if (type == 0x80 && !named)
                        {
                            _size[target] = BitConverter.ToUInt32(d, p + 16); // small file kept inside the index: no clusters
                        }
                        break;
                }
                pos += length;
            }
        }

        /// <summary>Adds up the clusters of a runlist that are really on the drive (not sparse).</summary>
        private static long UsedClusters(byte[] d, int pos, int end)
        {
            long used = 0;
            while (pos < end)
            {
                var header = d[pos++];
                if (header == 0) break;
                int lengthBytes = header & 0x0F, offsetBytes = header >> 4;
                if (lengthBytes == 0 || lengthBytes > 8 || offsetBytes > 8 || pos + lengthBytes + offsetBytes > end) break;
                long length = 0;
                for (var i = 0; i < lengthBytes; i++) length |= (long)d[pos + i] << (8 * i);
                pos += lengthBytes + offsetBytes;
                if (length <= 0) break;
                if (offsetBytes != 0) used += length;
            }
            return used;
        }

        private void FileName(byte[] d, int v, int end, int target)
        {
            if (v + 66 > end) return;
            var nameLength = d[v + 64];
            var nameSpace = d[v + 65];
            if (nameLength == 0 || v + 66 + nameLength * 2 > end) return;
            var parentRef = BitConverter.ToInt64(d, v);
            var parent = (int)(parentRef & 0x0000FFFFFFFFFFFF);
            var parentSeq = (ushort)((ulong)parentRef >> 48);
            var dos = nameSpace == 2;
            var has = (_flags[target] & HasName) != 0;
            var hasDos = (_flags[target] & DosName) != 0;

            if (dos && has) return; // the short 8.3 name of a file that has a long one
            var name = Encoding.Unicode.GetString(d, v + 66, nameLength * 2);
            if (!has || hasDos)
            {
                _name[target] = name;
                _parent[target] = parent;
                _parentSeq[target] = parentSeq;
                _flags[target] = (byte)((_flags[target] | HasName) & ~DosName | (dos ? DosName : 0));
                return;
            }
            if (dos) return;
            if (_parent[target] == parent && string.Equals(_name[target], name, StringComparison.Ordinal)) return;
            _links.Add((target, parent, parentSeq, name)); // another name for the same file: a hard link
        }

        private static long Ticks(long fileTime)
        {
            try { return fileTime > 0 && fileTime < 2650467743999999999 ? DateTime.FromFileTimeUtc(fileTime).Ticks : 0; }
            catch { return 0; }
        }

        // ------------------------------------------------------------------ the tree

        private Node Build(string rootName, CancellationToken token)
        {
            var count = _flags.Length;
            if (count <= Root || (_flags[Root] & (InUse | Dir)) != (InUse | Dir)) throw new InvalidDataException("The drive's top folder isn't in its file index.");
            var nodes = new Node[count];
            for (var i = 0; i < count; i++)
            {
                if ((_flags[i] & InUse) == 0 || ((_flags[i] & HasName) == 0 && i != Root)) continue;
                var isDir = (_flags[i] & Dir) != 0;
                nodes[i] = new Node
                {
                    Name = i == Root ? rootName : _name[i],
                    Children = isDir ? new List<Node>() : null,
                    Size = isDir ? 0 : _size[i],
                    Alloc = _alloc[i],
                    Mtime = isDir ? 0 : _mtime[i], // a folder's date is the newest file inside it
                    Attributes = (FileAttributes)_attr[i] | (isDir ? FileAttributes.Directory : 0),
                    Flags = isDir ? NodeFlags.Listed : NodeFlags.None
                };
            }
            token.ThrowIfCancellationRequested();

            var root = nodes[Root];
            Node lost = null;
            Node ParentOf(int parent, ushort parentSeq)
            {
                if (parent < 0 || parent >= count) return null;
                var p = nodes[parent];
                if (p == null || !p.IsDir || (parentSeq != 0 && _seq[parent] != 0 && _seq[parent] != parentSeq)) return null;
                return p;
            }
            void Attach(Node node, Node parent)
            {
                if (parent == null)
                {
                    if (lost == null)
                    {
                        lost = new Node { Name = "(Files without a folder)", Children = new List<Node>(), Flags = NodeFlags.Listed, Attributes = FileAttributes.Directory, Parent = root };
                        root.Children.Add(lost);
                    }
                    parent = lost;
                }
                node.Parent = parent;
                parent.Children.Add(node);
            }

            for (var i = 0; i < count; i++)
            {
                if (i == Root || nodes[i] == null) continue;
                Attach(nodes[i], ParentOf(_parent[i], _parentSeq[i]));
            }
            foreach (var link in _links)
            {
                var original = nodes[link.Record];
                if (original == null || original.IsDir) continue;
                var parent = ParentOf(link.Parent, link.ParentSeq);
                if (parent == null) continue;
                Attach(new Node
                {
                    Name = link.Name,
                    Size = original.Size,
                    Alloc = 0,
                    Mtime = original.Mtime,
                    Attributes = original.Attributes,
                    Flags = NodeFlags.HardLink
                }, parent);
            }
            token.ThrowIfCancellationRequested();

            // Totals, from the deepest folders up. Folders that loop back on themselves (a damaged index) are left out.
            var order = new List<Node>();
            var stack = new Stack<Node>();
            var visited = new HashSet<Node>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                if (!visited.Add(n)) continue;
                order.Add(n);
                if (n.IsDir) foreach (var c in n.Children) if (c.IsDir) stack.Push(c);
            }
            for (var i = order.Count - 1; i >= 0; i--)
            {
                var folder = order[i];
                foreach (var c in folder.Children)
                {
                    folder.Size += c.Size;
                    folder.Alloc += c.Alloc;
                    if (c.Mtime > folder.Mtime) folder.Mtime = c.Mtime;
                    if (c.IsDir)
                    {
                        folder.Files += c.Files;
                        folder.Folders += c.Folders + 1;
                    }
                    else folder.Files++;
                }
            }
            _name = null;
            return root;
        }
    }
}
