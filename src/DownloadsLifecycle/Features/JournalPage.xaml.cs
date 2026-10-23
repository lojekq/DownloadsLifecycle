using System.Diagnostics;
using System.IO;
using System.Windows;
using DownloadsLifecycle.Shell;

namespace DownloadsLifecycle.Features;

public sealed class JournalRow(JournalEntry e, string? state = null)
{
    public JournalEntry Entry { get; } = e;
    /// <summary>"Returned" / "Deleted permanently" instead of the buttons once Return or Restore no longer applies (<see cref="Settle"/>).</summary>
    public string? State { get; } = state is null ? null : Loc.T("Journal.State." + state);
    public string Time => Entry.T.ToLocalTime().ToString("g");
    public string Action => Loc.T("Op." + Entry.Op);
    public string Name => Path.GetFileName(Entry.Path);
    public string Where => Entry.To is null ? Entry.Path : $"{Entry.Path} → {Entry.To}";
    public string Reason => Fmt.Reason(Entry.Reason);
    public string By => Entry.By == "user" ? Loc.T("By.User") : string.Format(Loc.T("By.Rule"), Loc.T("Rule." + Entry.By.Split(':')[^1]));
    /// <summary>A move can go back while the file is still where it went (and it wasn't itself a return).</summary>
    public bool CanReturn { get; } = state is null && e.Op == "move" && e.Reason != "return" && e.To is not null && (Args.Current.Demo || Path.Exists(e.To));
    public bool InBin => Entry.Op == "recycle" && State is null;

    public override string ToString() => State is null ? $"{Action} {Name}" : $"{Action} {Name}, {State}";

    /// <summary>
    /// Null while Return / Restore still applies. "back": returned or restored later (by the journal, or by hand from the Recycle Bin);
    /// "gone": no longer in the Recycle Bin and not in its place, so deleted permanently. bin() is asked only when needed; null (Shell silent) claims nothing.
    /// </summary>
    public static string? Settle(JournalEntry e, IReadOnlyList<JournalEntry> journal, Func<List<(string Path, DateTime DeletedUtc)>?> bin, Func<string, bool> exists)
    {
        static bool Same(string a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        if (e.Op == "move" && e.Reason != "return")
            return journal.Any(x => x.Op == "move" && x.Reason == "return" && x.T > e.T && Same(x.Path, e.To)) ? "back" : null;
        if (e.Op != "recycle") return null;
        if (journal.Any(x => x.Op == "restore" && x.T > e.T && Same(x.Path, e.Path))) return "back";
        if (bin() is not { } b || RecycleBin.Holds(b, e.Path, e.T)) return null;
        return exists(e.Path) ? "back" : "gone";
    }
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
        var journal = Index.ReadJournal();
        List<(string, DateTime)>? bin = null;
        var asked = false;
        List<(string, DateTime)>? Bin()
        {
            if (!asked) (asked, bin) = (true, Args.Current.Demo ? null : RecycleBin.Contents()); // one walk through the Bin per fill
            return bin;
        }
        var rows = journal.Where(e => RulesOnly.IsChecked != true || e.By != "user")
            .Select(e => new JournalRow(e, JournalRow.Settle(e, journal, Bin, Path.Exists))).ToList();
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

    /// <summary>Opens the Recycle Bin.</summary>
    void BinClick(object sender, RoutedEventArgs e)
    {
        if (!Args.Current.Demo) Process.Start("explorer.exe", "shell:RecycleBinFolder");
    }
}
