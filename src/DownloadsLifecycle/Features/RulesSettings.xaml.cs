using System.Diagnostics;
using System.Windows;
using DownloadsLifecycle.Shell;

namespace DownloadsLifecycle.Features;

/// <summary>The app's section of Settings: folder, the two opt-in rules, safety limits, reminder, Explorer menu.</summary>
public partial class RulesSettings
{
    /// <summary>Page id "rules": Settings scrolled to this section (screenshots, the tray).</summary>
    public static bool ScrollIntoView { get; set; }

    public RulesSettings()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (!ScrollIntoView) return;
            ScrollIntoView = false;
            Dispatcher.BeginInvoke(() => BringIntoView(new Rect(0, 0, ActualWidth, ActualHeight * 0.75)), System.Windows.Threading.DispatcherPriority.Loaded);
        };
        var s = Settings.Current;
        FolderText.Text = Folder.Path;
        ExpiredSwitch.IsChecked = s.AutoExpired;
        DuplicatesSwitch.IsChecked = s.AutoDuplicates;
        GraceBox.Value = s.GraceHours;
        MaxSizeBox.Value = Math.Round(s.AutoMaxBytes / (double)(1L << 30), 1);
        MassBox.Value = s.MassLimit;
        for (var h = 0; h < 24; h++) TimeBox.Items.Add($"{h:00}:00");
        TimeBox.SelectedItem = $"{Reminder.At.Hours:00}:00";
        ReminderSwitch.IsChecked = s.Reminder;
        MenuSwitch.IsChecked = !Args.Current.Demo && ExplorerMenu.IsRegistered;

        ExpiredSwitch.Click += (_, _) => Save(() => s.AutoExpired = ExpiredSwitch.IsChecked == true);
        DuplicatesSwitch.Click += (_, _) => Save(() => s.AutoDuplicates = DuplicatesSwitch.IsChecked == true);
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

    void OpenFolder(object sender, RoutedEventArgs e)
    {
        if (!Args.Current.Demo) Process.Start("explorer.exe", $"\"{Folder.Path}\"");
    }
}
