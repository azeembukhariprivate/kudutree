using System;
using System.Collections.Generic;
using System.IO;

namespace Kudutree.Core
{
    /// <summary>
    /// Files and folders KUDUTREE won't delete: the drive's own file system records (the fast scan lists them, like $MFT),
    /// and the folders Windows needs to run. Deleting them would damage the drive or stop Windows from starting.
    /// </summary>
    public static class Protection
    {
        /// <summary>Where Windows keeps its own folders on this PC.</summary>
        public sealed class SystemFolders
        {
            /// <summary>The Windows folder, like C:\Windows (empty when unknown).</summary>
            public string Windows = string.Empty;
            /// <summary>The folder holding everyone's profiles, like C:\Users.</summary>
            public string Users = string.Empty;
            /// <summary>Folders Windows itself manages: Program Files, ProgramData and so on.</summary>
            public List<string> Managed = new List<string>();
        }

        // NTFS keeps these at the root of every drive; together they are the file system.
        private static readonly HashSet<string> Metafiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "$MFT", "$MFTMirr", "$LogFile", "$Volume", "$AttrDef", "$Bitmap", "$Boot", "$BadClus", "$Secure", "$UpCase",
            "$Extend", "$Quota", "$ObjId", "$Reparse", "$RmMetadata", "$TxfLog", "$Txf", "$Tops", "$Deleted"
        };

        // Folders Windows keeps at the root of every drive.
        private static readonly HashSet<string> DriveFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System Volume Information", "$Recycle.Bin"
        };

        // Files and folders Windows keeps at the root of the drive it runs from.
        private static readonly HashSet<string> SystemDriveItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "pagefile.sys", "hiberfil.sys", "swapfile.sys", "bootmgr", "BOOTNXT", "Boot", "Recovery", "DumpStack.log.tmp"
        };

        private static SystemFolders _current;

        public static SystemFolders Current => _current ?? (_current = Detect());

        private static SystemFolders Detect()
        {
            var folders = new SystemFolders();
            try
            {
                folders.Windows = Clean(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
                var profile = Clean(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                folders.Users = profile.Length > 0 ? Clean(Path.GetDirectoryName(profile) ?? string.Empty) : string.Empty;
                foreach (var f in new[]
                         {
                             Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                             Environment.SpecialFolder.CommonApplicationData
                         })
                {
                    var path = Clean(Environment.GetFolderPath(f));
                    if (path.Length > 0 && !folders.Managed.Contains(path)) folders.Managed.Add(path);
                }
                if (folders.Users.Length > 0) folders.Managed.Add(folders.Users);
            }
            catch (Exception)
            {
                // Unknown folders are simply not protected; the drive's own records still are.
            }
            return folders;
        }

        /// <summary>Why a file or folder can't be deleted, or null when it can.</summary>
        public static string Reason(Node node) => node == null || node.Parent == null ? "That's the whole scan." : Reason(node.Path, Current);

        public static string Reason(string path, SystemFolders system)
        {
            var p = Clean(path);
            var cut = p.LastIndexOf('\\');
            if (cut < 0) return null;
            var parent = p.Substring(0, cut);
            var name = p.Substring(cut + 1);
            var atDriveRoot = parent.Length == 2 && parent[1] == ':';
            var drive = p.Length >= 2 && p[1] == ':' ? p.Substring(0, 2) : null;

            if (atDriveRoot && Metafiles.Contains(name))
                return "\"" + name + "\" is part of the drive's file system, not a normal file. Deleting it would damage the drive.";
            if (drive != null && (Under(p, drive + "\\$Extend") || Under(p, drive + "\\System Volume Information")))
                return "This is part of the drive's file system, which Windows looks after. It can't be deleted here.";
            if (drive != null && Under(p, drive + "\\$Recycle.Bin", orSame: true))
                return "This is the Recycle Bin. Empty it from the Recycle Bin on the desktop instead.";
            if (atDriveRoot && DriveFolders.Contains(name))
                return "Windows keeps \"" + name + "\" on every drive. It can't be deleted here.";

            if (system.Windows.Length > 0)
            {
                var systemDrive = system.Windows.Length >= 2 && system.Windows[1] == ':' ? system.Windows.Substring(0, 2) : null;
                if (atDriveRoot && systemDrive != null && parent.Equals(systemDrive, StringComparison.OrdinalIgnoreCase) && SystemDriveItems.Contains(name))
                    return "Windows needs \"" + name + "\" to start and run. Use Settings or Disk Cleanup to make it smaller instead.";
                if (Under(p, system.Windows, orSame: true))
                    return "Windows needs this to run. Use Disk Cleanup (or Settings → System → Storage) to free space in the Windows folder safely.";
            }
            foreach (var managed in system.Managed)
            {
                if (p.Equals(managed, StringComparison.OrdinalIgnoreCase))
                    return "Windows needs the \"" + name + "\" folder itself. You can delete things inside it, and uninstall apps from Settings → Apps.";
            }
            if (system.Users.Length > 0 && parent.Equals(system.Users, StringComparison.OrdinalIgnoreCase))
                return "This is someone's whole profile folder. Delete things inside it instead, or remove the account in Settings → Accounts.";
            return null;
        }

        private static bool Under(string path, string folder, bool orSame = false) =>
            path.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase) || (orSame && path.Equals(folder, StringComparison.OrdinalIgnoreCase));

        private static string Clean(string path) => (path ?? string.Empty).Replace('/', '\\').TrimEnd('\\');
    }
}
