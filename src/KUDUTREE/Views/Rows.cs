using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using Kudutree.Core;

namespace Kudutree.Views
{
    /// <summary>A list that can be refilled in one go (one "reset" instead of a change per row), for big folders.</summary>
    public sealed class RowList<T> : ObservableCollection<T>
    {
        public void Replace(IEnumerable<T> items)
        {
            Items.Clear();
            foreach (var i in items) Items.Add(i);
            OnPropertyChanged(new PropertyChangedEventArgs("Count"));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }

    /// <summary>What every row shows; shared by the folder tree, the largest files and search results.</summary>
    public abstract class RowBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

        public static bool OnDisk = true;
        public static FileTypes Types;
    }

    /// <summary>A row of the folder tree (or the "more items" row at the end of a big folder).</summary>
    public sealed class TreeRow : RowBase
    {
        public Node Node { get; set; }
        public int Depth { get; set; }
        public bool Expanded { get; set; }
        /// <summary>The row standing for the rest of a big folder's contents.</summary>
        public Node MoreOf { get; set; }
        public int MoreCount { get; set; }
        public long MoreSize { get; set; }

        public bool IsMore => MoreOf != null;
        public Thickness Indent => new Thickness(Depth * 18, 0, 0, 0);

        public string Expander
        {
            get
            {
                if (IsMore || Node == null || !Node.IsDir || Node.Has(NodeFlags.Link)) return string.Empty;
                if (!Node.Has(NodeFlags.Listed)) return "…";
                if (Node.Children.Count == 0) return string.Empty;
                return Expanded ? "▾" : "▸";
            }
        }

        public Geometry Icon => IsMore ? Res("IconMore") : Node.IsDir ? Res(Node.Has(NodeFlags.Link) ? "IconLink" : "IconFolder") : null;
        public Brush Swatch => IsMore || Node.IsDir ? Brushes.Transparent : TypeBrush(Node);
        public Visibility SwatchVisible => !IsMore && !Node.IsDir ? Visibility.Visible : Visibility.Collapsed;
        public Visibility IconVisible => IsMore || Node.IsDir ? Visibility.Visible : Visibility.Collapsed;

        public string Name
        {
            get
            {
                if (IsMore) return $"{MoreCount:N0} smaller items";
                var name = Node.Name;
                if (Node.Has(NodeFlags.Unreadable)) name += "  (couldn't be read)";
                else if (Node.Has(NodeFlags.Link)) name += "  (link, not followed)";
                else if (Node.Has(NodeFlags.HardLink)) name += "  (hard link)";
                return name;
            }
        }

        public long Value => IsMore ? MoreSize : Node.Measure(OnDisk);
        public string SizeText => Format.Size(Value);

        private double Share
        {
            get
            {
                var parent = IsMore ? MoreOf : Node.Parent;
                var total = parent?.Measure(OnDisk) ?? 0;
                if (parent == null) return Value > 0 ? 1 : 0;
                return total <= 0 ? 0 : Math.Max(0, Math.Min(1, (double)Value / total));
            }
        }

        public double BarWidth => Share * 90;
        public string PercentText => Node?.Parent == null && !IsMore ? string.Empty : (Share * 100).ToString(Share >= 0.1 || Share == 0 ? "0" : "0.0") + "%";
        public string FilesText => IsMore ? string.Empty : Node.IsDir ? Node.Files.ToString("N0") : string.Empty;
        public string ModifiedText => IsMore || Node.Mtime <= 0 ? string.Empty : Node.Modified.ToString("d");
        public string Tip => IsMore ? "Click to show more" : Node.Path;

        internal static Geometry Res(string key) => Application.Current?.TryFindResource(key) as Geometry;

        private static readonly Dictionary<int, Brush> Brushes_ = new Dictionary<int, Brush>();

        public static Brush TypeBrush(Node node)
        {
            var rgb = Treemap.ColorOf(node, Types);
            if (!Brushes_.TryGetValue(rgb, out var b))
            {
                b = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
                b.Freeze();
                Brushes_[rgb] = b;
            }
            return b;
        }
    }

    /// <summary>A file or folder in the largest files list or search results.</summary>
    public sealed class ItemRow : RowBase
    {
        public Node Node { get; set; }

        public string Name => Node.Name;
        public string Folder => Node.Parent?.Path ?? string.Empty;
        public string SizeText => Format.Size(Node.Measure(OnDisk));
        public string ModifiedText => Node.Mtime <= 0 ? string.Empty : Node.Modified.ToString("d");
        public string FilesText => Node.IsDir ? Format.Count(Node.Files, "file") : string.Empty;
        public Brush Swatch => Node.IsDir ? System.Windows.Media.Brushes.Transparent : TreeRow.TypeBrush(Node);
        public Geometry Icon => Node.IsDir ? TreeRow.Res("IconFolder") : null;
        public Visibility SwatchVisible => Node.IsDir ? Visibility.Collapsed : Visibility.Visible;
        public Visibility IconVisible => Node.IsDir ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>A file type in the file types list.</summary>
    public sealed class TypeRow : RowBase
    {
        public FileType Type { get; set; }
        public long Total { get; set; }

        public string Name => Type.Title;
        public string Group => Type.Group + " · " + Format.Count(Type.Count, "file");
        public string SizeText => Format.Size(Type.Measure(OnDisk));
        public double BarWidth => Total <= 0 ? 0 : Math.Max(0, Math.Min(1, (double)Type.Measure(OnDisk) / Total)) * 60;
        public string PercentText => Total <= 0 ? string.Empty : ((double)Type.Measure(OnDisk) / Total * 100).ToString("0.0") + "%";
        public Brush Swatch
        {
            get
            {
                var rgb = Type.Color < 0 ? Treemap.Grey : Treemap.Palette[Type.Color % Treemap.Palette.Length];
                var b = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
                b.Freeze();
                return b;
            }
        }
    }

    /// <summary>Row templates, written in XAML and loaded at startup, so long lists only build the rows on screen.</summary>
    public static class Templates
    {
        private const string Ns = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";

        public static readonly GridLength[] TreeColumns = { new GridLength(1, GridUnitType.Star), new GridLength(100), new GridLength(150), new GridLength(80), new GridLength(100) };

        private static string Icon(string brush) =>
            "<Grid Width=\"14\" Height=\"14\" Margin=\"0,0,8,0\" VerticalAlignment=\"Center\">" +
            "<Border Width=\"11\" Height=\"11\" CornerRadius=\"2\" Background=\"{Binding Swatch}\" Visibility=\"{Binding SwatchVisible}\"/>" +
            "<Viewbox Visibility=\"{Binding IconVisible}\"><Canvas Width=\"24\" Height=\"24\"><Path Data=\"{Binding Icon}\" Stroke=\"{DynamicResource " + brush + "}\" StrokeThickness=\"1.8\" StrokeLineJoin=\"Round\"/></Canvas></Viewbox></Grid>";

        public static DataTemplate Tree => Parse(
            "<DataTemplate " + Ns + "><Grid Height=\"26\" ToolTip=\"{Binding Tip}\" Background=\"Transparent\">" +
            "<Grid.ColumnDefinitions><ColumnDefinition Width=\"*\"/><ColumnDefinition Width=\"100\"/><ColumnDefinition Width=\"150\"/><ColumnDefinition Width=\"80\"/><ColumnDefinition Width=\"100\"/></Grid.ColumnDefinitions>" +
            "<StackPanel Orientation=\"Horizontal\" Margin=\"{Binding Indent}\">" +
            "<TextBlock x:Name=\"Exp\" Text=\"{Binding Expander}\" Width=\"18\" FontSize=\"12\" Foreground=\"{DynamicResource SubtleText}\" VerticalAlignment=\"Center\" TextAlignment=\"Center\" Cursor=\"Hand\" Background=\"Transparent\"/>" +
            Icon("SubtleText") +
            "<TextBlock Text=\"{Binding Name}\" VerticalAlignment=\"Center\" TextTrimming=\"CharacterEllipsis\"/></StackPanel>" +
            "<TextBlock Grid.Column=\"1\" Text=\"{Binding SizeText}\" HorizontalAlignment=\"Right\" VerticalAlignment=\"Center\" Margin=\"0,0,12,0\" FontWeight=\"SemiBold\"/>" +
            "<Grid Grid.Column=\"2\" VerticalAlignment=\"Center\"><Border Width=\"90\" Height=\"10\" HorizontalAlignment=\"Left\" CornerRadius=\"2\" Background=\"{DynamicResource NeutralChipBg}\"/>" +
            "<Border Width=\"{Binding BarWidth}\" Height=\"10\" HorizontalAlignment=\"Left\" CornerRadius=\"2\" Background=\"{DynamicResource Accent}\" Opacity=\"0.75\"/>" +
            "<TextBlock Text=\"{Binding PercentText}\" Margin=\"98,0,0,0\" FontSize=\"11.5\" Foreground=\"{DynamicResource SubtleText}\"/></Grid>" +
            "<TextBlock Grid.Column=\"3\" Text=\"{Binding FilesText}\" HorizontalAlignment=\"Right\" VerticalAlignment=\"Center\" Margin=\"0,0,12,0\" Foreground=\"{DynamicResource SubtleText}\"/>" +
            "<TextBlock Grid.Column=\"4\" Text=\"{Binding ModifiedText}\" VerticalAlignment=\"Center\" Foreground=\"{DynamicResource SubtleText}\"/>" +
            "</Grid></DataTemplate>");

        public static DataTemplate Item => Parse(
            "<DataTemplate " + Ns + "><Grid Height=\"40\" Background=\"Transparent\" ToolTip=\"{Binding Folder}\">" +
            "<Grid.ColumnDefinitions><ColumnDefinition Width=\"*\"/><ColumnDefinition Width=\"100\"/><ColumnDefinition Width=\"90\"/><ColumnDefinition Width=\"100\"/></Grid.ColumnDefinitions>" +
            "<StackPanel Orientation=\"Horizontal\" Margin=\"6,0,0,0\">" + Icon("SubtleText") +
            "<StackPanel VerticalAlignment=\"Center\"><TextBlock Text=\"{Binding Name}\" TextTrimming=\"CharacterEllipsis\"/>" +
            "<TextBlock Text=\"{Binding Folder}\" FontSize=\"11.5\" Foreground=\"{DynamicResource SubtleText}\" TextTrimming=\"CharacterEllipsis\"/></StackPanel></StackPanel>" +
            "<TextBlock Grid.Column=\"1\" Text=\"{Binding SizeText}\" HorizontalAlignment=\"Right\" VerticalAlignment=\"Center\" Margin=\"0,0,12,0\" FontWeight=\"SemiBold\"/>" +
            "<TextBlock Grid.Column=\"2\" Text=\"{Binding FilesText}\" HorizontalAlignment=\"Right\" VerticalAlignment=\"Center\" Margin=\"0,0,12,0\" Foreground=\"{DynamicResource SubtleText}\"/>" +
            "<TextBlock Grid.Column=\"3\" Text=\"{Binding ModifiedText}\" VerticalAlignment=\"Center\" Foreground=\"{DynamicResource SubtleText}\"/>" +
            "</Grid></DataTemplate>");

        public static DataTemplate Type => Parse(
            "<DataTemplate " + Ns + "><Grid Height=\"38\" Background=\"Transparent\">" +
            "<Grid.ColumnDefinitions><ColumnDefinition Width=\"22\"/><ColumnDefinition Width=\"*\"/><ColumnDefinition Width=\"Auto\"/></Grid.ColumnDefinitions>" +
            "<Border Width=\"12\" Height=\"12\" CornerRadius=\"2\" Background=\"{Binding Swatch}\" VerticalAlignment=\"Center\" HorizontalAlignment=\"Left\" Margin=\"6,0,0,0\"/>" +
            "<StackPanel Grid.Column=\"1\" VerticalAlignment=\"Center\" Margin=\"4,0,0,0\"><TextBlock Text=\"{Binding Name}\" FontWeight=\"SemiBold\" TextTrimming=\"CharacterEllipsis\"/>" +
            "<TextBlock Text=\"{Binding Group}\" FontSize=\"11.5\" Foreground=\"{DynamicResource SubtleText}\" TextTrimming=\"CharacterEllipsis\"/></StackPanel>" +
            "<StackPanel Grid.Column=\"2\" VerticalAlignment=\"Center\" Margin=\"8,0,8,0\"><TextBlock Text=\"{Binding SizeText}\" HorizontalAlignment=\"Right\" FontWeight=\"SemiBold\"/>" +
            "<Grid HorizontalAlignment=\"Right\" Margin=\"0,3,0,0\"><Border Width=\"60\" Height=\"5\" CornerRadius=\"2\" Background=\"{DynamicResource NeutralChipBg}\"/>" +
            "<Border Width=\"{Binding BarWidth}\" Height=\"5\" CornerRadius=\"2\" HorizontalAlignment=\"Left\" Background=\"{Binding Swatch}\"/></Grid></StackPanel>" +
            "</Grid></DataTemplate>");

        private static DataTemplate Parse(string xaml) => (DataTemplate)XamlReader.Parse(xaml);
    }
}
