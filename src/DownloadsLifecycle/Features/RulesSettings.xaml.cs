using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using DownloadsLifecycle.Shell;
using Button = Wpf.Ui.Controls.Button;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace DownloadsLifecycle.Features;

/// <summary>
/// The app's section of Settings: watched folders, site rules (v1.0), the opt-in rules, safety limits, reminder, Explorer menu.
/// The folder and rule rows are built here, not in XAML: each row shows only the fields its action needs.
/// </summary>
public partial class RulesSettings
{
    /// <summary>Page id "rules": Settings scrolled to this section (screenshots, the tray).</summary>
    public static bool ScrollIntoView { get; set; }

    static Settings S => Settings.Current;
    static string Telegram => Path.Combine(Folder.Path, "Telegram Desktop");

    public RulesSettings()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (!ScrollIntoView) return;
            ScrollIntoView = false;
            Dispatcher.BeginInvoke(() => BringIntoView(new Rect(0, 0, ActualWidth, ActualHeight * 0.3)), System.Windows.Threading.DispatcherPriority.Loaded);
        };
        var s = S;
        FolderText.Text = Folder.Path;
        ExpiredSwitch.IsChecked = s.AutoExpired;
        DuplicatesSwitch.IsChecked = s.AutoDuplicates;
        InstallersSwitch.IsChecked = s.AutoInstallers;
        ArchivesSwitch.IsChecked = s.AutoArchives;
        ExactDaysBox.Value = s.ExactDays;
        GraceBox.Value = s.GraceHours;
        MaxSizeBox.Value = Math.Round(s.AutoMaxBytes / (double)(1L << 30), 1);
        MassBox.Value = s.MassLimit;
        for (var h = 0; h < 24; h++) TimeBox.Items.Add($"{h:00}:00");
        TimeBox.SelectedItem = $"{Reminder.At.Hours:00}:00";
        ReminderSwitch.IsChecked = s.Reminder;
        MenuSwitch.IsChecked = !Args.Current.Demo && ExplorerMenu.IsRegistered;
        if (!Args.Current.Demo && StorageSense.DownloadsDays() is { } days)
        {
            StorageSenseBar.Message = string.Format(Loc.T("StorageSense.Message"), Fmt.Plural("Ttl.Days", days));
            StorageSenseBar.IsOpen = true;
        }
        ExactNotes();
        Folders();
        RulesList();

        ExpiredSwitch.Click += (_, _) => Save(() => s.AutoExpired = ExpiredSwitch.IsChecked == true);
        DuplicatesSwitch.Click += (_, _) => Save(() => s.AutoDuplicates = DuplicatesSwitch.IsChecked == true);
        InstallersSwitch.Click += (_, _) => Save(() => s.AutoInstallers = InstallersSwitch.IsChecked == true);
        ArchivesSwitch.Click += (_, _) => Save(() => s.AutoArchives = ArchivesSwitch.IsChecked == true);
        ExactDaysBox.ValueChanged += (_, _) => { Save(() => s.ExactDays = Math.Max(1, (int)(ExactDaysBox.Value ?? 3))); ExactNotes(); };
        GraceBox.ValueChanged += (_, _) => Save(() => s.GraceHours = (int)(GraceBox.Value ?? 24));
        MaxSizeBox.ValueChanged += (_, _) => Save(() => s.AutoMaxBytes = (long)((MaxSizeBox.Value ?? 4) * (1L << 30)));
        MassBox.ValueChanged += (_, _) => Save(() => s.MassLimit = Math.Max(1, (int)(MassBox.Value ?? 20)));
        TimeBox.SelectionChanged += (_, _) => Save(() => { s.ReminderTime = (string)TimeBox.SelectedItem; s.RemindedOn = null; });
        ReminderSwitch.Click += (_, _) => Save(() => s.Reminder = ReminderSwitch.IsChecked == true);
        MenuSwitch.Click += (_, _) =>
        {
            s.ContextMenu = MenuSwitch.IsChecked == true;
            Settings.Save();
            try { if (s.ContextMenu) ExplorerMenu.Register(); else ExplorerMenu.Unregister(); }
            catch (Exception e) { Log.Error("context menu", e); }
        };
    }

    static void Save(Action change)
    {
        change();
        Settings.Save();
    }

    void ExactNotes()
    {
        InstallersNote.Text = string.Format(Loc.T("Rules.Installers.Note"), Fmt.Plural("Ttl.Days", S.ExactDays));
        ArchivesNote.Text = string.Format(Loc.T("Rules.Archives.Note"), Fmt.Plural("Ttl.Days", S.ExactDays));
    }

    void OpenFolder(object sender, RoutedEventArgs e)
    {
        if (!Args.Current.Demo) Process.Start("explorer.exe", $"\"{Folder.Path}\"");
    }

    // ---- v1.0 extra folders ----

    void Folders()
    {
        ExtraList.Items.Clear();
        foreach (var dir in S.ExtraFolders.ToList())
        {
            var remove = new Button { Icon = new SymbolIcon(SymbolRegular.Delete24), ToolTip = Loc.T("Folders.Remove") };
            System.Windows.Automation.AutomationProperties.SetName(remove, Loc.T("Folders.Remove"));
            remove.Click += (_, _) => { S.ExtraFolders.Remove(dir); Changed(); };
            ExtraList.Items.Add(new CardControl
            {
                Icon = new SymbolIcon(SymbolRegular.FolderOpen24), Margin = new Thickness(0, 0, 0, 4),
                Header = new System.Windows.Controls.TextBlock { Text = dir, TextTrimming = TextTrimming.CharacterEllipsis },
                Content = remove,
            });
        }
        TelegramButton.Visibility = Directory.Exists(Telegram) && !S.ExtraFolders.Contains(Telegram, StringComparer.OrdinalIgnoreCase)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    void Changed()
    {
        Settings.Save();
        Engine.Refolder();
        Folders();
    }

    void AddFolder(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = Loc.T("Folders.Add") };
        if (d.ShowDialog() != true) return;
        if (Folder.Allowed(d.FolderName) is { } why) { System.Windows.MessageBox.Show(Loc.T(why), AppInfo.DisplayName); return; }
        if (!S.ExtraFolders.Contains(d.FolderName, StringComparer.OrdinalIgnoreCase)) S.ExtraFolders.Add(d.FolderName);
        Changed();
    }

    void AddTelegram(object sender, RoutedEventArgs e)
    {
        S.ExtraFolders.Add(Telegram);
        Changed();
    }

    // ---- v1.0 site rules ----

    void RulesList()
    {
        RuleList.Items.Clear();
        foreach (var r in S.SiteRules.ToList()) RuleList.Items.Add(RuleRow(r));
    }

    void AddRule(object sender, RoutedEventArgs e)
    {
        S.SiteRules.Add(new SiteRule());
        Settings.Save();
        RulesList();
    }

    static void RuleSaved()
    {
        Settings.Save();
        Engine.Redetect();
    }

    FrameworkElement RuleRow(SiteRule r)
    {
        var domain = new TextBox { Text = r.Domain, PlaceholderText = Loc.T("SiteRules.Domain"), Width = 180, Margin = new Thickness(0, 0, 8, 6) };
        var exts = new TextBox { Text = r.Extensions, PlaceholderText = Loc.T("SiteRules.Extensions"), Width = 140, Margin = new Thickness(0, 0, 8, 6) };
        var action = new ComboBox { Width = 190, Margin = new Thickness(0, 0, 8, 6) };
        foreach (var a in new[] { "ttl", "move", "clean" }) action.Items.Add(new ComboBoxItem { Tag = a, Content = Loc.T("SiteRules.Action." + a) });
        action.SelectedItem = action.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == r.Action) ?? action.Items[0];
        var days = new NumberBox { Value = r.Days, Minimum = 1, Maximum = 3650, MaxDecimalPlaces = 0, Width = 110, ClearButtonEnabled = false, Margin = new Thickness(0, 0, 8, 6) };
        var folder = new Button { Content = r.Folder ?? Loc.T("SiteRules.Folder"), Margin = new Thickness(0, 0, 8, 6), MaxWidth = 260 };
        var auto = new ToggleSwitch { IsChecked = r.Auto, Content = Loc.T("SiteRules.Auto"), Margin = new Thickness(0, 0, 8, 6) };
        var remove = new Button { Icon = new SymbolIcon(SymbolRegular.Delete24), ToolTip = Loc.T("SiteRules.Remove"), Margin = new Thickness(0, 0, 0, 6) };
        foreach (var (c, name) in new (FrameworkElement, string)[] { (domain, "SiteRules.Domain"), (exts, "SiteRules.Extensions"), (action, "SiteRules.Title"),
                     (days, "Ttl.Pick"), (folder, "SiteRules.Folder"), (auto, "SiteRules.Auto"), (remove, "SiteRules.Remove") })
            System.Windows.Automation.AutomationProperties.SetName(c, Loc.T(name));

        void Sync()
        {
            var a = (string)((ComboBoxItem)action.SelectedItem).Tag;
            days.Visibility = a == "ttl" ? Visibility.Visible : Visibility.Collapsed;
            folder.Visibility = a == "move" ? Visibility.Visible : Visibility.Collapsed;
            auto.Visibility = a == "clean" ? Visibility.Collapsed : Visibility.Visible; // offering for cleanup is never automatic
        }
        Sync();
        domain.LostFocus += (_, _) => { r.Domain = domain.Text.Trim(); RuleSaved(); };
        exts.LostFocus += (_, _) => { r.Extensions = exts.Text.Trim(); RuleSaved(); };
        action.SelectionChanged += (_, _) => { r.Action = (string)((ComboBoxItem)action.SelectedItem).Tag; Sync(); RuleSaved(); };
        days.ValueChanged += (_, _) => { r.Days = Math.Max(1, (int)(days.Value ?? 7)); RuleSaved(); };
        auto.Click += (_, _) => { r.Auto = auto.IsChecked == true; RuleSaved(); };
        folder.Click += (_, _) =>
        {
            var d = new OpenFolderDialog { Title = Loc.T("SiteRules.Folder") };
            if (d.ShowDialog() != true) return;
            r.Folder = d.FolderName;
            folder.Content = d.FolderName;
            RuleSaved();
        };
        remove.Click += (_, _) => { S.SiteRules.Remove(r); RuleSaved(); RulesList(); };

        var row = new WrapPanel();
        foreach (var c in new FrameworkElement[] { domain, exts, action, days, folder, auto, remove })
        {
            c.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(c);
        }
        return new Card { Content = row, Margin = new Thickness(0, 0, 0, 4), Padding = new Thickness(12, 10, 12, 4) };
    }
}
