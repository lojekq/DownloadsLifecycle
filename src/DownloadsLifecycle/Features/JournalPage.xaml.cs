using System.Diagnostics;
using System.IO;
using System.Windows;
using DownloadsLifecycle.Shell;

namespace DownloadsLifecycle.Features;

public sealed class JournalRow(JournalEntry e)
{
    public JournalEntry Entry { get; } = e;
    public string Time => Entry.T.ToLocalTime().ToString("g");
    public string Action => Loc.T("Op." + Entry.Op);
    public string Name => Path.GetFileName(Entry.Path);
    public string Where => Entry.To is null ? Entry.Path : $"{Entry.Path} → {Entry.To}";
    public string Reason => Fmt.Reason(Entry.Reason);
    public string By => Entry.By == "user" ? Loc.T("By.User") : string.Format(Loc.T("By.Rule"), Loc.T("Rule." + Entry.By.Split(':')[^1]));
    /// <summary>A move can go back while the file is still where it went (and it wasn't itself a return).</summary>
    public bool CanReturn { get; } = e.Op == "move" && e.Reason != "return" && e.To is not null && (Args.Current.Demo || Path.Exists(e.To));
    public bool InBin => Entry.Op == "recycle";

    public override string ToString() => $"{Action} {Name}";
}

/// <summary>What happened, when, why and by whom (the user or a rule). Moves can be returned; recycled files are in the Recycle Bin.</summary>
public partial class JournalPage
{
    public JournalPage()
    {
        InitializeComponent();
        Loaded += (_, _) => { Index.Journaled += Changed; Fill(); };
        Unloaded += (_, _) => Index.Journaled -= Changed;
    }

    void Changed() => Dispatcher.BeginInvoke(Fill);

    void Fill()
    {
        var rows = Index.ReadJournal().Where(e => RulesOnly.IsChecked != true || e.By != "user").Select(e => new JournalRow(e)).ToList();
        Table.ItemsSource = rows;
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void FilterClick(object sender, RoutedEventArgs e) => Fill();

    void RestoreClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is JournalRow r) Actions.Restore(r.Entry);
    }

    void ReturnClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is JournalRow r) Actions.Return(r.Entry);
    }

    /// <summary>v1.0 restores from the Recycle Bin itself; for now it opens it.</summary>
    void BinClick(object sender, RoutedEventArgs e)
    {
        if (!Args.Current.Demo) Process.Start("explorer.exe", "shell:RecycleBinFolder");
    }
}
