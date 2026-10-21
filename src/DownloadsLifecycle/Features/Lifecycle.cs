using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using DownloadsLifecycle.Shell;

namespace DownloadsLifecycle.Features;

/// <summary>App start: engine, --ttl pipe, context menu, tray items, settings section and the exit/remove hooks.</summary>
public static class Lifecycle
{
    static MenuItem? sortItem;

    public static void Start(Application app)
    {
        SettingsPage.AppSection = () => new RulesSettings();
        App.RemoveData = () => { ExplorerMenu.Unregister(); return null; };
        if (Args.Current.Demo) return; // DemoData fills the snapshot; nothing runs, nothing is written
        Engine.Start(app.Dispatcher);
        TtlPipe.Serve();
        if (Install.IsInstalled && Settings.Current.ContextMenu) ExplorerMenu.Ensure(); // Debug builds: only by the switch
        App.Exiting += () =>
        {
            Actions.CancelPending();
            TtlPipe.Stop();
            Engine.Stop();
        };
    }

    public static IEnumerable<Control> TrayItems()
    {
        sortItem = Tray.Item(Loc.T("Tray.Sort"), () => App.ShowMain("inbox"));
        var pause = new MenuItem { Header = Loc.T("Tray.Pause") };
        MenuItem hour = Tray.Item(Loc.T("Tray.Pause.Hour"), () => Engine.PausedUntilUtc = DateTime.UtcNow.AddHours(1)),
            restart = Tray.Item(Loc.T("Tray.Pause.Restart"), () => Engine.PausedUntilUtc = DateTime.MaxValue),
            resume = Tray.Item(Loc.T("Tray.Pause.Resume"), () => Engine.PausedUntilUtc = default);
        pause.Items.Add(hour);
        pause.Items.Add(restart);
        pause.Items.Add(resume);
        pause.SubmenuOpened += (_, _) => resume.IsEnabled = Engine.Paused;
        Engine.Changed += UpdateTray;
        UpdateTray();
        return
        [
            sortItem,
            Tray.Item(Loc.T("Tray.OpenFolder"), () => Process.Start("explorer.exe", $"\"{Folder.Path}\"")),
            pause,
            Tray.Item(Loc.T("Page.Settings"), () => App.ShowMain("rules")),
        ];
    }

    /// <summary>"Sort inbox (12)" and the tooltip "Inbox: 12 · to clean 3.4 GB".</summary>
    static void UpdateTray()
    {
        var (count, _, bytes) = Summary(Engine.Current);
        if (sortItem is not null) sortItem.Header = count > 0 ? $"{Loc.T("Tray.Sort")} ({count})" : Loc.T("Tray.Sort");
        // ponytail: the frozen Shell/Tray has no tooltip setter, so the private icon is reached by reflection; add Tray.ToolTip if a second app needs it
        if (typeof(Tray).GetField("icon", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) is TaskbarIcon icon)
            icon.ToolTipText = count > 0 ? string.Format(Loc.T("Tray.Tooltip"), count, Fmt.Size(bytes)) : AppInfo.DisplayName;
    }

    /// <summary>Inbox size, to-clean count and bytes.</summary>
    public static (int Inbox, int ToClean, long Bytes) Summary(Snapshot s)
    {
        var now = DateTime.UtcNow;
        var visible = s.Items.Where(i => !Actions.IsHidden(i.Key)).ToList();
        var clean = visible.Where(i => Rules.ToClean(i, now)).ToList();
        return (visible.Count(i => Rules.InInbox(i, s.LastTriageUtc, now, Folder.Day)), clean.Count, clean.Sum(i => i.Size));
    }
}

/// <summary>Daily balloon "Inbox: 12 files, 3.4 GB to clean". Not during a fullscreen game or presentation: an hour later then.</summary>
public static class Reminder
{
    static DateTime nextTryUtc;

    public static TimeSpan At => TimeSpan.TryParse(Settings.Current.ReminderTime, out var t) ? t : new TimeSpan(19, 0, 0);

    public static void Check(Snapshot s)
    {
        var st = Settings.Current;
        var now = DateTime.Now;
        var today = now.ToString("yyyy-MM-dd");
        if (!st.Reminder || Args.Current.Demo || now.TimeOfDay < At || st.RemindedOn == today || DateTime.UtcNow < nextTryUtc) return;
        var (inbox, _, bytes) = Lifecycle.Summary(s);
        if (inbox == 0) return;
        if (UserBusy.Now) { nextTryUtc = DateTime.UtcNow.AddHours(1); return; }
        Tray.Notify(AppInfo.DisplayName, string.Format(Loc.T("Notify.Reminder"), Fmt.Files(inbox), Fmt.Size(bytes)), "inbox");
        st.RemindedOn = today;
        Settings.Save();
    }
}
