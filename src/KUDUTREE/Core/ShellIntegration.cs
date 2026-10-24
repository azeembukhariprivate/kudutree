using System;
using Microsoft.Win32;

namespace Kudutree.Core
{
    /// <summary>
    /// "Show sizes with KUDUTREE" on the right-click menu of folders and drives in File Explorer. For the current user only
    /// (HKEY_CURRENT_USER), so no administrator rights are needed. On Windows 11 it's under "Show more options".
    /// </summary>
    public static class ShellIntegration
    {
        private static readonly string[] Keys = { @"Software\Classes\Directory\shell\KUDUTREE", @"Software\Classes\Drive\shell\KUDUTREE" };

        public static void Register()
        {
            try
            {
                foreach (var path in Keys)
                    using (var key = Registry.CurrentUser.CreateSubKey(path))
                    {
                        key.SetValue(string.Empty, "Show sizes with KUDUTREE");
                        key.SetValue("Icon", "\"" + AppInfo.ExePath + "\",0");
                        using (var command = key.CreateSubKey("command"))
                            command.SetValue(string.Empty, $"\"{AppInfo.ExePath}\" --scan \"%1\"");
                    }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.Security.SecurityException) { }
        }

        public static void Remove()
        {
            foreach (var path in Keys)
            {
                try { Registry.CurrentUser.DeleteSubKeyTree(path, false); }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.Security.SecurityException) { }
            }
        }

        public static bool IsRegistered
        {
            get
            {
                try { using (var key = Registry.CurrentUser.OpenSubKey(Keys[0] + @"\command")) return key != null; }
                catch { return false; }
            }
        }

        public static string Command(bool drive = false)
        {
            try { using (var key = Registry.CurrentUser.OpenSubKey(Keys[drive ? 1 : 0] + @"\command")) return key?.GetValue(string.Empty) as string; }
            catch { return null; }
        }

        /// <summary>Keeps the menu in step with the setting, and pointing at this copy of KUDUTREE.</summary>
        public static void Refresh()
        {
            if (Settings.ShellMenu) Register();
            else if (IsRegistered) Remove();
        }
    }
}
