using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Kudutree.Controls;
using Kudutree.Core;

namespace Kudutree.Views
{
    public sealed class SettingsWindow : Window
    {
        public SettingsWindow(Window owner)
        {
            Width = 560;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Dialogs.Prepare(this, "Settings · KUDUTREE", owner);

            var root = new StackPanel { Margin = new Thickness(26, 22, 26, 22) };
            root.Children.Add(Ui.Heading("Settings", 20).Margin(0, 0, 0, 14));

            root.Children.Add(Ui.Heading("Appearance", 14).Margin(0, 0, 0, 8));
            var themes = new WrapPanel();
            foreach (var mode in new[] { ThemeMode.System, ThemeMode.Light, ThemeMode.Dark })
            {
                var chip = new RadioButton { Content = mode == ThemeMode.System ? "Same as Windows" : mode.ToString(), GroupName = "theme", IsChecked = Settings.Theme == mode }
                    .Styled("ChipRadio");
                chip.Checked += (s, e) =>
                {
                    Settings.Theme = mode;
                    ThemeManager.Apply(mode);
                };
                themes.Children.Add(chip);
            }
            root.Children.Add(themes);

            root.Children.Add(Ui.Heading("Deleting", 14).Margin(0, 16, 0, 4));
            var confirm = new CheckBox { Content = "Ask before moving items to the Recycle Bin", IsChecked = Settings.ConfirmRecycle };
            confirm.Click += (s, e) => Settings.ConfirmRecycle = confirm.IsChecked == true;
            root.Children.Add(confirm);
            root.Children.Add(Ui.Subtle("Deleting permanently always asks first.", 12).Margin(27, 0, 0, 0));

            root.Children.Add(Ui.Heading("File Explorer", 14).Margin(0, 16, 0, 4));
            var menu = new CheckBox { Content = "Add \"Show sizes with KUDUTREE\" to folders' and drives' right-click menu", IsChecked = Settings.ShellMenu };
            menu.Click += (s, e) =>
            {
                Settings.ShellMenu = menu.IsChecked == true;
                ShellIntegration.Refresh();
            };
            root.Children.Add(menu);
            root.Children.Add(Ui.Subtle("For your Windows account only. On Windows 11 it's under \"Show more options\".", 12).Margin(27, 0, 0, 0));

            root.Children.Add(Ui.Heading("Updates", 14).Margin(0, 16, 0, 4));
            var updates = new CheckBox { Content = "Check for a new KUDUTREE version when it starts", IsChecked = Settings.CheckForUpdates };
            updates.Click += (s, e) => Settings.CheckForUpdates = updates.IsChecked == true;
            root.Children.Add(updates);
            root.Children.Add(Ui.Subtle("Only asks GitHub for the latest version number. Nothing is downloaded without asking.", 12).Margin(27, 0, 0, 0));

            root.Children.Add(Ui.Divider().Margin(0, 18, 0, 0));
            var bottom = Ui.Columns(Ui.Star(), Ui.Auto);
            bottom.Children.Add(Ui.Link("About KUDUTREE", (s, e) => About.Show(this)).At(0, 0));
            var close = Ui.Button("Close", null, "PrimaryButton");
            close.IsCancel = true;
            close.Click += (s, e) => Close();
            bottom.Children.Add(close.At(0, 1));
            root.Children.Add(bottom.Margin(0, 14, 0, 0));
            Content = root;
        }
    }

    public static class About
    {
        public static Window Build(Window owner)
        {
            var header = Ui.Row(LogoTile.Create(64), Ui.Column(Ui.Text("KUDUTREE", 20, bold: true), Ui.Subtle("Version " + AppInfo.VersionText + " · See what takes up space", 12.5))
                .Margin(16, 0, 0, 0));
            ((FrameworkElement)header.Children[1]).VerticalAlignment = VerticalAlignment.Center;

            var rows = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
            void Row(string label, UIElement value)
            {
                var g = Ui.Columns(Ui.Px(120), Ui.Star());
                g.Children.Add(Ui.Subtle(label, 12.5, false).At(0, 0));
                g.Children.Add(value.At(0, 1));
                rows.Children.Add(new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 7, 0, 7), Child = g }.Res(Border.BorderBrushProperty, "BorderBrush"));
            }
            var madeBy = Ui.Text("Azeem, with Claude and ", 12.5);
            var heart = new Run("♥");
            heart.SetResourceReference(TextElement.ForegroundProperty, "Accent");
            madeBy.Inlines.Add(heart);
            Row("Made by", madeBy);
            Row("Licence", Ui.Row(Ui.Text("MIT · free and open source  ", 12.5), Ui.Link("View", (s, e) => OpenLocal("LICENSE.txt"))));
            Row("Source code", Ui.Link("github.com/" + AppInfo.RepoOwner + "/" + AppInfo.RepoName, (s, e) => AppInfo.OpenUrl(AppInfo.RepoUrl)));
            Row("Scanning", Ui.Text("Its own engine: reads NTFS file indexes directly, or lists folders", 12.5));
            Row("Third-party", Ui.Link("Third-party notices", (s, e) => OpenLocal("THIRD-PARTY-NOTICES.txt")));
            rows.Children.Add(Ui.Divider());

            var content = Ui.Column(header, rows,
                Ui.Subtle("KUDUTREE only reads your drives. Nothing is changed unless you delete something yourself, and moving to the Recycle Bin can be undone.", 11.5).Margin(0, 14, 0, 0));

            Window window = null;
            var updates = Ui.Button("Check for updates");
            var close = Ui.Button("Close", null, "PrimaryButton");
            close.IsCancel = true;
            close.IsDefault = true;
            window = Dialogs.Dialog(owner, "About KUDUTREE", content, 560, updates, close);
            close.Click += (s, e) => window.Close();
            updates.Click += async (s, e) =>
            {
                updates.IsEnabled = false;
                try
                {
                    var info = await UpdateChecker.CheckAsync();
                    if (info == null) Dialogs.Info(window, "Couldn't find any published version yet.");
                    else if (info.IsNewer)
                    {
                        if (Dialogs.Confirm(window, $"KUDUTREE {info.Latest.ToString(3)} is available. Open the download page?")) AppInfo.OpenUrl(info.Url);
                    }
                    else Dialogs.Info(window, "You have the latest version.");
                }
                catch
                {
                    Dialogs.Error(window, "Couldn't check for updates. Check your internet connection and try again.");
                }
                finally
                {
                    updates.IsEnabled = true;
                }
            };
            return window;
        }

        public static void Show(Window owner) => Build(owner).ShowDialog();

        private static void OpenLocal(string name)
        {
            var path = Path.Combine(Path.GetDirectoryName(AppInfo.ExePath) ?? string.Empty, name);
            if (File.Exists(path)) AppInfo.OpenFile(path);
            else AppInfo.OpenUrl(AppInfo.RepoUrl);
        }
    }
}
