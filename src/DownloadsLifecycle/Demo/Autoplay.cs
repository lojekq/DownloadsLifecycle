using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DownloadsLifecycle.Features;

namespace DownloadsLifecycle.Demo;

/// <summary>--autoplay for gif.ps1 (08 §10): in the inbox pick the duplicate, an installer and the extracted zip, let them go, the undo bar fades.</summary>
public static class Autoplay
{
    public static void Start(MainWindow w)
    {
        Actions.UndoWindow = TimeSpan.FromSeconds(3); // the GIF is 8 s: the bar has to go within it
        // wherever the real cursor rests, no tooltip pops up in the recording
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.ToolTipOpeningEvent, new ToolTipEventHandler((_, e) => e.Handled = true));
        // Seconds after the window is on screen; gif.ps1 starts recording when --rect-file appears, ffmpeg needs ~1 s
        (double At, Action Do)[] steps =
        [
            (1.8, () => InboxPage.Current?.Select(r => r.Item.Suggestion?.Type == "duplicate")),
            (2.5, () => InboxPage.Current?.Select(r => r.Item.Suggestion?.Type == "installer" && r.Name.StartsWith("VSCode"))),
            (3.2, () => InboxPage.Current?.Select(r => r.Item.Suggestion?.Type == "archive")),
            (4.2, () => InboxPage.Current?.ReleaseSelected()),
        ];
        var started = DateTime.UtcNow;
        var next = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) =>
        {
            while (next < steps.Length && (DateTime.UtcNow - started).TotalSeconds >= steps[next].At) steps[next++].Do();
            if (next == steps.Length) timer.Stop();
        };
        timer.Start();
    }
}
