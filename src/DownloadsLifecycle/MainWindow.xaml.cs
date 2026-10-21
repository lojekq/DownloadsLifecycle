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
