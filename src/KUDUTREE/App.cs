using System;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using Kudutree.Core;
using Kudutree.Views;

namespace Kudutree
{
    public static class Program
    {
        /// <summary>
        /// KUDUTREE.exe                     open
        /// KUDUTREE.exe --scan "D:\x"       open and scan a folder or drive (the right-click menu)
        /// KUDUTREE.exe --scan C:\ --fast   the fast scan (used after restarting as administrator)
        /// KUDUTREE.exe --register-shell | --unregister   used by the installer
        /// KUDUTREE.exe --self-test         automated checks for the build
        /// </summary>
        [STAThread]
        public static int Main(string[] args)
        {
            bool Has(string name) => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            string Value(string name)
            {
                for (var i = 0; i < args.Length - 1; i++)
                    if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
                return null;
            }

            var app = new App();
            if (Has("--self-test")) return app.RunSelfTest();
            if (Has("--register-shell"))
            {
                Settings.ShellMenu = true;
                ShellIntegration.Register();
                return 0;
            }
            if (Has("--unregister"))
            {
                ShellIntegration.Remove();
                return 0;
            }
            return app.Run(Value("--scan"), Has("--fast"));
        }
    }

    public sealed class App : Application
    {
        public int Run(string scan, bool fast)
        {
            Startup += (s, e) =>
            {
                Init();
                var main = new MainWindow();
                MainWindow = main;
                main.Show();
                ShellIntegration.Refresh();
                if (!string.IsNullOrEmpty(scan)) main.StartScan(scan, fast ? true : (bool?)null);
            };
            return Run();
        }

        public int RunSelfTest()
        {
            var code = 1;
            Startup += (s, e) =>
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                DispatcherUnhandledException += OnUnhandledException;
                code = SelfTest.Run();
                Shutdown(code);
            };
            Run();
            return code;
        }

        internal void Init()
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            DispatcherUnhandledException += OnUnhandledException;
            ThemeManager.Init(Settings.Theme);
            ThemeManager.WatchSystem();
        }

        private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            if (SelfTest.Running)
            {
                SelfTest.Errors.Add(e.Exception.ToString());
                e.Handled = true;
                return;
            }
            Dialogs.Error(MainWindow?.IsVisible == true ? MainWindow : null, "Something went wrong:\n\n" + e.Exception.Message);
            e.Handled = true;
        }
    }
}
