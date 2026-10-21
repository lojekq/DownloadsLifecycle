using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;
using DownloadsLifecycle.Shell;

namespace DownloadsLifecycle.Features;

/// <summary>"Move…" and "Lifetime ▾" menus shared by the inbox and the file list.</summary>
public static class Menus
{
    public static void Show(ContextMenu m, UIElement anchor)
    {
        m.PlacementTarget = anchor;
        m.Placement = PlacementMode.Bottom;
        m.IsOpen = true;
    }

    /// <summary>Recent folders, then "Choose a folder…".</summary>
    public static ContextMenu Move(IReadOnlyList<Item> items)
    {
        var m = new ContextMenu();
        foreach (var dir in Settings.Current.RecentFolders.Where(Directory.Exists))
            m.Items.Add(Tray.Item(dir, () => Actions.MoveTo(items, dir)));
        if (m.Items.Count > 0) m.Items.Add(new Separator());
        m.Items.Add(Tray.Item(Loc.T("Move.Choose"), () =>
        {
            var d = new OpenFolderDialog { Title = Loc.T("Move.Title") };
            if (d.ShowDialog() == true) Actions.MoveTo(items, d.FolderName);
        }));
        return m;
    }

    public static ContextMenu Lifetime(IReadOnlyList<Item> items, UIElement anchor)
    {
        var keys = items.Select(i => i.Key).ToList();
        var m = new ContextMenu();
        foreach (var d in new[] { 1, 7, 30 })
            m.Items.Add(Tray.Item(Fmt.Plural("Ttl.Days", d), () => Engine.Apply(keys, new("days", d))));
        m.Items.Add(Tray.Item(Loc.T("Ttl.Pick"), () => PickDate(keys, anchor)));
        m.Items.Add(new Separator());
        m.Items.Add(Tray.Item(Loc.T("Ttl.Clear"), () => Engine.Apply(keys, new("clear"))));
        return m;
    }

    static void PickDate(List<string> keys, UIElement anchor)
    {
        var cal = new Calendar { DisplayDateStart = DateTime.Today.AddDays(1), SelectionMode = CalendarSelectionMode.SingleDate };
        var popup = new Popup { Child = cal, PlacementTarget = anchor, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true };
        cal.SelectedDatesChanged += (_, _) =>
        {
            if (cal.SelectedDate is { } date) Engine.Apply(keys, new("date", UntilLocal: date));
            popup.IsOpen = false;
        };
        popup.IsOpen = true;
    }

    public static void Keep(IReadOnlyList<Item> items) => Engine.Apply(items.Select(i => i.Key), new("keep"));
}
