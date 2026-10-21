using System.Windows;
using System.Windows.Controls;
using DownloadsLifecycle.Features;
using DownloadsLifecycle.Shell;

namespace DownloadsLifecycle;

public partial class MainWindow
{
    public MainWindow()
    {
        InitializeComponent();
        ElevatedBar.IsOpen = AppInfo.IsElevated;
        // WPF-UI wraps every Page in its own DynamicScrollViewer unless the page says ScrollViewer.CanContentScroll="False".
        // Our pages scroll themselves (a ScrollViewer, or a list in a * row): inside that wrapper they get infinite height,
        // never scroll, and their ScrollViewer still marks the mouse wheel handled, so nothing scrolls at all.
        Nav.Navigating += (_, e) => { if (e.Page is DependencyObject page) ScrollViewer.SetCanContentScroll(page, false); };
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
        if (page == "rules") (RulesSettings.ScrollIntoView, page) = (true, "settings"); // Settings, scrolled to the app's section
        Nav.Navigate(page);
    }

    public void ShowUpdate(string version, string url)
    {
        UpdateBar.Message = string.Format(Loc.T("Update.Message"), version);
        UpdateLink.NavigateUri = url;
        UpdateBar.IsOpen = true;
    }
}
