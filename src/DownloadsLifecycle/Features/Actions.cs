using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using DownloadsLifecycle.Shell;

namespace DownloadsLifecycle.Features;

/// <summary>
/// Let go / move / return, on the UI thread (Shell operations need STA and an owner window). Every action re-checks the file first
/// (<see cref="SafetyGate"/>) and goes into journal.jsonl. "Let go" waits 8 s for Undo; on exit unconfirmed ones are dropped, the file stays.
/// </summary>
public static class Actions
{
    static readonly Dictionary<string, Item> Pending = [];   // let go, still undoable
    static readonly Dictionary<string, Item> Waiting = [];   // let go, but in use: retried every 30 s for an hour
    static readonly DispatcherTimer Retry = new() { Interval = TimeSpan.FromSeconds(30) };
    static DispatcherTimer? undo;
    static DateTime waitingSince;
    static (int Count, long Bytes) autoSummary;

    public static TimeSpan UndoWindow { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>Hidden from the lists: waiting for Undo or for the file to be closed.</summary>
    public static bool IsHidden(string key) => Pending.ContainsKey(key) || Waiting.ContainsKey(key);

    /// <summary>Undo bar changed: (count, bytes) of the batch still undoable, or (0, 0) when the bar should go.</summary>
    public static event Action<int, long>? UndoChanged;
    /// <summary>A message for the user (shown on the current page).</summary>
    public static event Action<string>? Notice;

    static nint Owner => Application.Current?.MainWindow is { } w ? new WindowInteropHelper(w).Handle : 0;
    static Settings S => Settings.Current;

    /// <summary>"Let go": into the Recycle Bin after the undo window. A new batch commits the previous one first.</summary>
    public static void Release(IEnumerable<Item> items)
    {
        Commit();
        foreach (var i in items) Pending[i.Key] = i;
        if (Pending.Count == 0) return;
        undo = new DispatcherTimer { Interval = UndoWindow };
        undo.Tick += (_, _) => Commit();
        undo.Start();
        UndoChanged?.Invoke(Pending.Count, Pending.Values.Sum(i => i.Size));
        Engine.Refresh();
    }

    public static void Undo()
    {
        undo?.Stop();
        Pending.Clear();
        UndoChanged?.Invoke(0, 0);
        Engine.Refresh();
    }

    /// <summary>The undo window is over (or a new batch came): to the Recycle Bin now.</summary>
    public static void Commit()
    {
        undo?.Stop();
        if (Pending.Count == 0) return;
        var batch = Pending.Values.ToList();
        Pending.Clear();
        var failed = new List<(Item, Verdict)>();
        foreach (var i in batch)
            switch (Recycle(i, auto: false, "user"))
            {
                case Verdict.Ok or Verdict.Gone: break;
                case Verdict.InUse: Wait(i); break;
                case var v: failed.Add((i, v)); break;
            }
        UndoChanged?.Invoke(0, 0);
        Engine.Refresh();
        if (failed.Count > 0) Notice?.Invoke(string.Join("\n", failed.Select(f => $"{f.Item1.Name}: {Loc.T("Verdict." + f.Item2)}")));
    }

    /// <summary>On exit: unconfirmed ones stay where they are (08 §5).</summary>
    public static void CancelPending()
    {
        undo?.Stop();
        Pending.Clear();
        Waiting.Clear();
    }

    static void Wait(Item i)
    {
        if (Waiting.Count == 0) waitingSince = DateTime.UtcNow;
        Waiting[i.Key] = i;
        if (Retry.IsEnabled) return;
        Retry.Tick -= RetryTick;
        Retry.Tick += RetryTick;
        Retry.Start();
    }

    static void RetryTick(object? sender, EventArgs e)
    {
        foreach (var i in Waiting.Values.ToList())
            if (Recycle(i, auto: false, "user") != Verdict.InUse || DateTime.UtcNow - waitingSince > TimeSpan.FromHours(1))
                Waiting.Remove(i.Key);
        if (Waiting.Count == 0) Retry.Stop();
        Engine.Refresh();
    }

    /// <summary>Opt-in rules, from the worker's plan: no undo window (the Recycle Bin and the journal are the undo), silent while a game runs.</summary>
    public static void RunAuto(List<(Item Item, string Rule)> plan)
    {
        IReadOnlyList<UninstallEntry>? installed = null;
        foreach (var (i, rule) in plan)
        {
            if (rule == "rule:duplicate" && !Engine.Current.Items.Any(k => k.Key != i.Key && k.Name == i.Suggestion?.Subject && k.Sha256 == i.Sha256 && File.Exists(k.Path)))
                continue; // the copy that stays must still be there
            // exact-match rules (v1.0) look again right before: still installed by ProductCode, still extracted to the last file
            if (rule == "rule:installer" && Installers.Check(i, installed ??= UninstallEntries.List()) is not { Confidence: "exact", Why: "msi" }) continue;
            if (rule == "rule:archive" && Archives.Check(i.Path) is not { Percent: 100 }) continue;
            if (Recycle(i, auto: true, rule) == Verdict.Ok) autoSummary = (autoSummary.Count + 1, autoSummary.Bytes + i.Size);
        }
        if (autoSummary.Count == 0 || UserBusy.Now) return; // shown later, once the game is over
        Tray.Notify(AppInfo.DisplayName, string.Format(Loc.T("Notify.Auto"), Fmt.Files(autoSummary.Count), Fmt.Size(autoSummary.Bytes)), "journal");
        autoSummary = default;
    }

    static Verdict Recycle(Item i, bool auto, string by)
    {
        var now = DateTime.UtcNow;
        var v = Args.Current.Demo ? Verdict.Ok : SafetyGate.Check(Facts.Observe(i, now), auto, S.GraceHours, S.AutoMaxBytes);
        if (v == Verdict.Gone)
        {
            Index.Append(new(now, "gone", i.Path, i.Size, Rules.Reason(i, now), by)); // quarantined by an antivirus, deleted by hand: no error
            Engine.Forget(i.Key);
            return v;
        }
        if (v != Verdict.Ok) { Log.Info($"skipped: {v}"); return v; }
        if (!Recycler.Recycle(i.Path, Owner)) return Verdict.Changed;
        Index.Append(new(now, "recycle", i.Path, i.Size, Rules.Reason(i, now), by, Sha256: i.Sha256));
        Engine.Forget(i.Key);
        return Verdict.Ok;
    }

    /// <summary>"Move…": same volume = rename, another volume = copy, verify, then the source to the Recycle Bin.</summary>
    public static void MoveTo(IEnumerable<Item> items, string dir)
    {
        S.RecentFolders.RemoveAll(d => string.Equals(d, dir, StringComparison.OrdinalIgnoreCase));
        S.RecentFolders.Insert(0, dir);
        if (S.RecentFolders.Count > 5) S.RecentFolders.RemoveRange(5, S.RecentFolders.Count - 5);
        Settings.Save();

        var errors = new List<string>();
        foreach (var i in items)
        {
            var v = Args.Current.Demo ? Verdict.Ok : SafetyGate.Check(Facts.Observe(i, DateTime.UtcNow) with { FixedDrive = true }, auto: false, 0, long.MaxValue);
            if (v != Verdict.Ok) errors.Add($"{i.Name}: {Loc.T("Verdict." + v)}");
            else if (Move(i, dir, "user", Rules.Reason(i, DateTime.UtcNow)) is { } error) errors.Add(error);
        }
        if (errors.Count > 0) Notice?.Invoke(string.Join("\n", errors));
    }

    /// <summary>One move with its journal line; the error text for the user, or null.</summary>
    static string? Move(Item i, string dir, string by, string reason)
    {
        if (string.Equals(Path.GetDirectoryName(i.Path), Path.GetFullPath(dir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return null; // already there
        try
        {
            var to = Mover.Move(i.Path, dir, i.IsDir, Owner);
            Index.Append(new(DateTime.UtcNow, "move", i.Path, i.Size, reason, by, to, i.Sha256));
            Engine.Forget(i.Key);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Log.Warn("move failed: " + Fmt.Err(e));
            return $"{i.Name}: {e.Message}";
        }
    }

    /// <summary>
    /// v1.0 site rules in auto mode: a file goes to the rule's folder once it's ready (not written, not open; not before 10 min after start).
    /// A folder that is gone, a kept file or a failed move stop the rule for that file; "in use" waits for the next pass.
    /// </summary>
    public static void RunRuleMoves(List<(Item Item, string Folder)> moves)
    {
        foreach (var (i, dir) in moves)
        {
            var v = SafetyGate.Check(Facts.Observe(i, DateTime.UtcNow) with { FixedDrive = true }, auto: true, 0, long.MaxValue);
            if (v is Verdict.InUse or Verdict.Changed) continue;
            if (v != Verdict.Ok || !Directory.Exists(dir) || Move(i, dir, "rule:site", $"site|{i.Site}") is not null)
            {
                Engine.MarkRuled(i.Key);
                if (!Directory.Exists(dir)) Notice?.Invoke(string.Format(Loc.T("Rule.FolderGone"), dir));
            }
        }
    }

    /// <summary>The inbox's Apply on a site rule that waits for the user (v1.0): move to its folder or give its lifetime.</summary>
    public static void ApplyRule(Item i)
    {
        if (i.Suggestion is not { Type: "rule" } s) return;
        if (s.Why == "move" && s.Arg is { } dir)
        {
            if (Directory.Exists(dir)) MoveTo([i], dir);
            else Notice?.Invoke(string.Format(Loc.T("Rule.FolderGone"), dir));
            return;
        }
        if (int.TryParse(s.Arg, out var days)) Engine.Apply([i.Key], new("days", days));
        Engine.MarkRuled(i.Key);
    }

    /// <summary>v1.0 journal "Restore": a file let go goes back from the Recycle Bin to where it was.</summary>
    public static void Restore(JournalEntry e)
    {
        if (Path.Exists(e.Path)) { Notice?.Invoke(Loc.T("Journal.RestoreTaken")); return; }
        if (RecycleBin.Restore(e.Path, e.T)) Index.Append(new(DateTime.UtcNow, "restore", e.Path, e.Size, "", "user"));
        else Notice?.Invoke(Loc.T("Journal.RestoreGone"));
    }

    /// <summary>Journal "Return": a moved file goes back to where it was (a free name if that one is taken).</summary>
    public static void Return(JournalEntry e)
    {
        if (e.To is null || !Path.Exists(e.To)) { Notice?.Invoke(Loc.T("Journal.ReturnGone")); return; }
        try
        {
            var back = Mover.Move(e.To, Path.GetDirectoryName(e.Path)!, Directory.Exists(e.To), Owner);
            Index.Append(new(DateTime.UtcNow, "move", e.To, e.Size, "return", "user", back, e.Sha256));
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Log.Warn("return failed: " + Fmt.Err(x));
            Notice?.Invoke(x.Message);
        }
    }
}
