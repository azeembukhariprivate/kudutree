using System;
using System.IO;

namespace Kudutree.Core
{
    public static class Paths
    {
        public static readonly bool IsWindows = Environment.OSVersion.Platform == PlatformID.Win32NT;

        public static bool IsUnc(string path) => !string.IsNullOrEmpty(path) && path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\", StringComparison.Ordinal);

        /// <summary>"E:" for "E:\Stuff", or null.</summary>
        public static string DriveOf(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Length < 2 || path[1] != ':' || !char.IsLetter(path[0])) return null;
            return char.ToUpperInvariant(path[0]) + ":";
        }

        /// <summary>True for "E:\" (a whole drive), false for a folder.</summary>
        public static bool IsDriveRoot(string path) => !string.IsNullOrEmpty(path) && path.Length <= 3 && DriveOf(path) != null;

        /// <summary>A tidy absolute path without a trailing separator (except "E:\").</summary>
        public static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            path = path.Trim().Trim('"');
            if (IsWindows) path = path.Replace('/', '\\');
            if (IsUnc(path)) return @"\\" + path.TrimStart('\\').TrimEnd('\\');
            if (path.Length == 2 && path[1] == ':') path += "\\";
            try { path = Path.GetFullPath(path); }
            catch { /* checked when scanning */ }
            if (path.Length > 3 || (!IsWindows && path.Length > 1)) path = path.TrimEnd('\\', '/');
            if (path.Length == 2 && path[1] == ':') path += "\\";
            return path;
        }

        /// <summary>Adds "\\?\" (or "\\?\UNC\") on Windows so paths of any length work.</summary>
        public static string Long(string path)
        {
            if (!IsWindows || string.IsNullOrEmpty(path) || path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
            if (IsUnc(path)) return @"\\?\UNC\" + path.TrimStart('\\');
            if (path.Length >= 2 && path[1] == ':') return @"\\?\" + path;
            return path;
        }

        public static string Combine(string folder, string name) =>
            folder.EndsWith("\\") || folder.EndsWith("/") ? folder + name : folder + Path.DirectorySeparatorChar + name;
    }
}
