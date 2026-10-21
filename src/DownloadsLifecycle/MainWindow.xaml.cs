using System.Windows;
using DownloadsLifecycle.Features;
using DownloadsLifecycle.Shell;

namespace DownloadsLifecycle;

public partial class MainWindow
{
    public MainWindow()
    {
        InitializeComponent();
        ElevatedBar.IsOpen = AppInfo.IsElevated;
        Loaded += (_, _) => { if (Nav.SelectedItem is null) Navigate("inbox"); };
        UndoButton.Click += (_, _) => Actions.Undo();
        // "2 h ago" and "ends in 5 min" move on even when nothing on disk changes
        var minute = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        minute.Tick += (_, _) => { if (IsVisible) Engine.Refresh(); };
        minute.Start();
        Actions.UndoChanged += (count, bytes) =>
        {
            UndoBar.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
            UndoText.Text = string.Format(Loc.T("Inbox.Released"), Fmt.Files(count), Fmt.Size(bytes));
        };
        Actions.Notice += text =>
        {
            NoticeBar.Message = text;
            NoticeBar.IsOpen = true;
        };
    }

    /// <summary>By TargetPageTag: "inbox", "files", "journal", "settings", "about"; "rules" = Settings at the app's section.</summary>
    public void Navigate(string page)
    {
        if (page is "rules" or "safety") (RulesSettings.ScrollTo, page) = (page, "settings"); // Settings, scrolled to the app's section
        Nav.Navigate(page);
    }

    public void ShowUpdate(string version, string url)
    {
        UpdateBar.Message = string.Format(Loc.T("Update.Message"), version);
        UpdateLink.NavigateUri = url;
        UpdateBar.IsOpen = true;
    }
}
