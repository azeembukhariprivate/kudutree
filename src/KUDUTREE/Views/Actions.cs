using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Kudutree.Core;
using Microsoft.VisualBasic.FileIO;

namespace Kudutree.Views
{
    /// <summary>Things you can do with a file or folder in the results.</summary>
    public static class Actions
    {
        public static void Open(Node node)
        {
            if (node.IsDir) AppInfo.OpenFolder(node.Path);
            else AppInfo.OpenFile(node.Path);
        }

        public static void ShowInExplorer(Node node)
        {
            if (node.Parent == null) AppInfo.OpenFolder(node.Path);
            else AppInfo.OpenFolder(node.Parent.Path, node.Path);
        }

        public static void CopyPath(Node node) => Controls.Ui.Copy(node.Path);

        public static bool Exists(Node node)
        {
            var path = Paths.Long(node.Path);
            return node.IsDir ? Directory.Exists(path) : File.Exists(path);
        }

        /// <summary>
        /// Moves a file or folder to the Recycle Bin, or deletes it permanently, using Windows' own delete (so Windows'
        /// messages appear if something can't be deleted). Runs on its own thread so the window stays responsive.
        /// Returns null when it worked, or what went wrong.
        /// </summary>
        public static Task<string> Delete(Node node, bool permanent)
        {
            var path = node.Path;
            var isDir = node.IsDir;
            var done = new TaskCompletionSource<string>();
            var thread = new Thread(() =>
            {
                try
                {
                    var recycle = permanent ? RecycleOption.DeletePermanently : RecycleOption.SendToRecycleBin;
                    if (isDir) FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, recycle, UICancelOption.ThrowException);
                    else FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, recycle, UICancelOption.ThrowException);
                    done.SetResult(null);
                }
                catch (OperationCanceledException)
                {
                    done.SetResult("Cancelled.");
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException || ex is ArgumentException || ex is NotSupportedException)
                {
                    done.SetResult(ex.Message);
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            return done.Task;
        }
    }
}
