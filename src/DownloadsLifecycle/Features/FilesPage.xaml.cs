using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DownloadsLifecycle.Shell;

namespace DownloadsLifecycle.Features;

/// <summary>Every file of the folder: search by name, site and URL, group by site, filter chips, details with both URLs.</summary>
public partial class FilesPage
{
    /// <summary>--demo screenshots: start grouped and with this file selected.</summary>
    public static (bool Group, string? Select) Preset { get; set; }

    public FilesPage()
    {
        InitializeComponent();
        GroupSwitch.IsChecked = Preset.Group;
        Loaded += (_, _) => { Engine.Changed += Fill; Fill(); };
        Unloaded += (_, _) => Engine.Changed -= Fill;
    }

    void Fill()
    {
        var now = DateTime.UtcNow;
        var keep = Table.SelectedItems.Cast<Row>().Select(r => r.Key).ToHashSet();
        if (keep.Count == 0 && Preset.Select is { } name) keep = Engine.Current.Items.Where(i => i.Name == name).Select(i => i.Key).ToHashSet();
        var items = Engine.Current.Items.Where(i => !Actions.IsHidden(i.Key)).ToList();
        var bySite = items.GroupBy(i => i.Site).ToDictionary(g => g.Key ?? "", g => g.Count());
        var rows = items
            .OrderBy(i => i.Site is null ? 1 : 0).ThenByDescending(i => bySite[i.Site ?? ""]).ThenBy(i => i.Site).ThenByDescending(i => i.CreatedUtc)
            .Select(i => new Row(i, now)).ToList();
        var view = new ListCollectionView(rows) { Filter = o => Matches((Row)o) };
        var grouped = GroupSwitch.IsChecked == true;
        if (grouped) view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Row.SiteText)));
        SiteColumn.Visibility = grouped ? Visibility.Collapsed : Visibility.Visible; // the group header already says it
        Table.ItemsSource = view;
        foreach (var r in rows.Where(r => keep.Contains(r.Key))) Table.SelectedItems.Add(r);
        CountText.Text = Fmt.Files(view.Count);
        ShowDetails();
    }

    bool Matches(Row r)
    {
        var i = r.Item;
        var chips = new[] { (ChipInstallers, i.Kind == Kind.Installer), (ChipArchives, i.Kind == Kind.Archive),
            (ChipDuplicates, i.Suggestion?.Type == "duplicate"), (ChipNoSource, i.Site is null) };
        if (chips.Any(c => c.Item1.IsChecked == true) && !chips.Any(c => c.Item1.IsChecked == true && c.Item2)) return false;
        var q = Search.Text.Trim();
        return q.Length == 0 || new[] { i.Name, i.Site, i.Src?.Referrer, i.Src?.Url, ZoneReader.Host(i.Src?.Url) }
            .Any(s => s?.Contains(q, StringComparison.OrdinalIgnoreCase) == true);
    }

    void FilterChanged(object sender, RoutedEventArgs e)
    {
        if (Table.ItemsSource is ListCollectionView v) { v.Refresh(); CountText.Text = Fmt.Files(v.Count); }
    }

    void GroupChanged(object sender, RoutedEventArgs e) => Fill();
    void SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowDetails();

    void ShowDetails()
    {
        var row = Table.SelectedItem as Row;
        Details.DataContext = row;
        Side.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        SideColumn.Width = row is null ? new GridLength(0) : new GridLength(280);
    }

    List<Item> Selection => Table.SelectedItems.Cast<Row>().Select(r => r.Item).ToList();

    void CopyReferrer(object sender, RoutedEventArgs e) { if (Table.SelectedItem is Row { Item.Src.Referrer: { } u }) Clipboard.SetText(u); }
    void CopyUrl(object sender, RoutedEventArgs e) { if (Table.SelectedItem is Row { Item.Src.Url: { } u }) Clipboard.SetText(u); }

    void OpenFolder(object sender, RoutedEventArgs e)
    {
        if (Table.SelectedItem is Row r && !Args.Current.Demo) Process.Start("explorer.exe", $"/select,\"{r.Item.Path}\"");
    }

    void KeepClick(object sender, RoutedEventArgs e) => Menus.Keep(Selection);
    void TtlClick(object sender, RoutedEventArgs e) => Menus.Show(Menus.Lifetime(Selection, (UIElement)sender), (UIElement)sender);
    void MoveClick(object sender, RoutedEventArgs e) => Menus.Show(Menus.Move(Selection), (UIElement)sender);
    void ReleaseClick(object sender, RoutedEventArgs e) => Actions.Release(Selection);
}
