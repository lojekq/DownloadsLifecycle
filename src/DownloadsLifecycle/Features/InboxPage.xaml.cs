using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DownloadsLifecycle.Shell;

namespace DownloadsLifecycle.Features;

/// <summary>The triage screen: new files, cleanup offers, lifetimes ending within a day. Keep / Move / To Recycle Bin / Lifetime, K M Del T.</summary>
public partial class InboxPage
{
    public static InboxPage? Current { get; private set; }

    public InboxPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Current = this;
            Engine.Changed += Fill;
            Fill();
        };
        Unloaded += (_, _) =>
        {
            Engine.Changed -= Fill;
        };
    }

    List<Item> toClean = [];

    void Fill()
    {
        var now = DateTime.UtcNow;
        var s = Engine.Current;
        var selected = List.SelectedItems.Cast<Row>().Select(r => r.Key).ToHashSet();
        var visible = s.Items.Where(i => !Actions.IsHidden(i.Key)).ToList();
        var rows = visible.Where(i => Rules.InInbox(i, s.LastTriageUtc, now, Folder.Day))
            .OrderByDescending(i => Rules.ToClean(i, now)).ThenBy(i => i.TtlUtc ?? DateTime.MaxValue).ThenByDescending(i => i.CreatedUtc)
            .Select(i => new Row(i, now)).ToList();
        List.ItemsSource = rows;
        foreach (var r in rows.Where(r => selected.Contains(r.Key))) List.SelectedItems.Add(r);

        toClean = visible.Where(i => Rules.ToClean(i, now)).ToList();
        SummaryText.Text = toClean.Count > 0
            ? string.Format(Loc.T("Inbox.Summary"), Fmt.Files(toClean.Count), Fmt.Size(toClean.Sum(i => i.Size)))
            : Loc.T("Inbox.NothingToClean");
        AcceptButton.IsEnabled = toClean.Count > 0;
        var (freedCount, freedBytes) = Rules.FreedThisMonth(Index.ReadJournal(), DateTime.Now);
        FreedText.Text = freedCount > 0 ? string.Format(Loc.T("Inbox.Freed"), Fmt.Size(freedBytes), Fmt.Files(freedCount)) : "";
        SeenButton.Visibility = rows.Any(r => r.Item.FirstSeenUtc > s.LastTriageUtc && r.Item.TtlUtc is null && r.Item.Suggestion is null)
            ? Visibility.Visible : Visibility.Collapsed;

        Welcome.Visibility = Settings.Current.WelcomeDone ? Visibility.Collapsed : Visibility.Visible;
        if (!Settings.Current.WelcomeDone)
            WelcomeText.Text = string.Format(Loc.T("Welcome.Found"), Fmt.Files(s.Items.Count), Fmt.Size(s.Items.Sum(i => i.Size)), s.Items.Count(i => i.Site is not null));
        EmptyPanel.Visibility = rows.Count == 0 && Settings.Current.WelcomeDone ? Visibility.Visible : Visibility.Collapsed;
        NextReminderText.Text = Settings.Current.Reminder ? string.Format(Loc.T("Inbox.NextReminder"), Reminder.At.ToString(@"hh\:mm")) : "";
    }

    /// <summary>A row button acts on the whole selection when its row is part of it, else on that row only.</summary>
    List<Item> Targets(object sender)
    {
        var row = (Row)((FrameworkElement)sender).DataContext;
        var sel = List.SelectedItems.Cast<Row>().ToList();
        return (sel.Contains(row) ? sel : [row]).Select(r => r.Item).ToList();
    }

    List<Item> Selection => List.SelectedItems.Cast<Row>().Select(r => r.Item).ToList();

    void ApplyClick(object sender, RoutedEventArgs e) { foreach (var i in Targets(sender).Where(i => i.Suggestion?.Type == "rule")) Actions.ApplyRule(i); }
    void KeepClick(object sender, RoutedEventArgs e) => Menus.Keep(Targets(sender));
    void MoveClick(object sender, RoutedEventArgs e) => Menus.Show(Menus.Move(Targets(sender)), (UIElement)sender);
    void TtlClick(object sender, RoutedEventArgs e) => Menus.Show(Menus.Lifetime(Targets(sender), (UIElement)sender), (UIElement)sender);
    void ReleaseClick(object sender, RoutedEventArgs e) => Release(Targets(sender));
    void SeenClick(object sender, RoutedEventArgs e) => Engine.MarkSeen();

    public void Release(IReadOnlyList<Item> items)
    {
        if (items.Count == 0) return;
        Actions.Release(items);
    }

    /// <summary>For --autoplay: select the rows that match.</summary>
    public void Select(Func<Row, bool> which)
    {
        foreach (var r in List.Items.Cast<Row>().Where(which)) List.SelectedItems.Add(r);
    }

    public void ReleaseSelected() => Release(Selection);

    async void AcceptClick(object sender, RoutedEventArgs e)
    {
        var items = toClean.ToList();
        if (items.Count == 0) return;
        var list = string.Join("\n", items.Take(12).Select(i => $"{i.Name} — {Fmt.Size(i.Size)}"))
            + (items.Count > 12 ? "\n" + string.Format(Loc.T("Confirm.More"), items.Count - 12) : "");
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = Loc.T("Confirm.Title"),
            Content = new ScrollViewer
            {
                MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = string.Format(Loc.T("Confirm.Text"), Fmt.Files(items.Count), Fmt.Size(items.Sum(i => i.Size))) + "\n\n" + list },
            },
            PrimaryButtonText = Loc.T("Inbox.Release"),
            CloseButtonText = Loc.T("Confirm.Cancel"),
            Owner = Window.GetWindow(this), // over the app, not on the primary monitor
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        if (await box.ShowDialogAsync() == Wpf.Ui.Controls.MessageBoxResult.Primary) Release(items);
    }

    void WelcomeClick(object sender, RoutedEventArgs e)
    {
        var s = Settings.Current;
        s.AutoExpired = WelcomeExpired.IsChecked == true;
        s.AutoDuplicates = WelcomeDuplicates.IsChecked == true;
        s.WelcomeDone = true;
        Settings.Save();
        Engine.MarkSeen();
        Fill();
    }

    void ListKeyDown(object sender, KeyEventArgs e)
    {
        var sel = Selection;
        if (sel.Count == 0 || e.OriginalSource is TextBox) return;
        var anchor = List.ItemContainerGenerator.ContainerFromItem(List.SelectedItems[0]) as UIElement ?? List;
        switch (e.Key)
        {
            case Key.K: Menus.Keep(sel); break;
            case Key.M: Menus.Show(Menus.Move(sel), anchor); break;
            case Key.T: Menus.Show(Menus.Lifetime(sel, anchor), anchor); break;
            case Key.Delete: Release(sel); break;
            default: return;
        }
        e.Handled = true;
    }
}
