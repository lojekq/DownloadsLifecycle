using System.Collections.Concurrent;
using System.IO;
using System.Windows.Threading;
using DownloadsLifecycle.Shell;

namespace DownloadsLifecycle.Features;

/// <summary>
/// The one background worker (08 §5). It alone owns the index (no locks): watcher events, the 5 s debounce, detectors and the
/// 15 min tick run here one by one at BelowNormal priority. The UI reads immutable <see cref="Snapshot"/>s and asks with <see cref="Post"/>.
/// Shell operations (Recycle Bin) go back to the UI thread.
/// </summary>
public static class Engine
{
    static readonly BlockingCollection<Action> Jobs = [];
    static readonly CancellationTokenSource Stopping = new();
    static readonly Dictionary<string, string> Checked = [];   // key → stamp/generation of the last installer or zip check
    static Index index = new();
    static readonly List<(Scanner Scan, FileSystemWatcher Watch)> Folders = [];   // one per watched folder, worker only
    static IReadOnlyList<UninstallEntry> uninstall = [];
    static int generation;
    static DateTime saveDue = DateTime.MaxValue, detectDue = DateTime.MaxValue, nextTick;
    static Thread? thread;
    static Dispatcher? ui;

    /// <summary>Latest snapshot, replaced on the UI thread right before <see cref="Changed"/>.</summary>
    public static Snapshot Current { get; private set; } = Snapshot.Empty;
    public static event Action? Changed;

    public static DateTime StartedUtc { get; private set; } = DateTime.UtcNow;
    public static bool FirstRun { get; private set; }

    /// <summary>"Pause auto-rules": until this time; DateTime.MaxValue = until restart.</summary>
    public static DateTime PausedUntilUtc { get; set; }
    public static bool Paused => DateTime.UtcNow < PausedUntilUtc;

    /// <summary>Lists redraw (something hidden came back), on the UI thread.</summary>
    public static void Refresh() => Changed?.Invoke();

    /// <summary>--demo: the UI shows these items and nothing runs.</summary>
    public static void ShowDemo(Snapshot s) { Current = s; Changed?.Invoke(); }

    public static void Start(Dispatcher dispatcher)
    {
        ui = dispatcher;
        StartedUtc = DateTime.UtcNow;
        Snapshot? saved = null;
        try { saved = Index.Load(Index.StatePath); }
        catch (Exception e) { Log.Error("state.json not readable: " + Fmt.Err(e)); } // locked: start empty this time
        FirstRun = saved is null;
        index = Index.From(saved ?? new([], DateTime.UtcNow)); // first run: what is already there is not "new"
        index.Dirty = FirstRun;
        Current = index.Snapshot();
        Index.RotateJournal(DateTime.UtcNow);

        thread = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "worker" };
        thread.Start();
        Post(() =>
        {
            uninstall = UninstallEntries.List();
            Watch(Folder.All);
        });
    }

    /// <summary>Settings changed the extra folders: watch the new list; items of dropped folders leave the index (and their lifetimes with them).</summary>
    public static void Refolder()
    {
        var all = Folder.All;
        Post(() =>
        {
            foreach (var gone in index.Items.Values.Where(i => !all.Any(f => Scanner.IsTopLevel(f, i.Path))).Select(i => i.Key).ToList()) index.Remove(gone);
            Watch(all);
        });
    }

    static void Watch(IReadOnlyList<string> folders)
    {
        foreach (var (_, w) in Folders) w.Dispose();
        Folders.Clear();
        foreach (var dir in folders)
        {
            try
            {
                var scan = new Scanner(index, dir);
                scan.Reconcile();
                var w = new FileSystemWatcher(dir)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
                    InternalBufferSize = 64 * 1024,
                };
                void Touch(string path) => Post(() => scan.Touch(path, DateTime.UtcNow));
                w.Created += (_, e) => Touch(e.FullPath);
                w.Changed += (_, e) => Touch(e.FullPath);
                w.Deleted += (_, e) => Touch(e.FullPath);
                w.Renamed += (_, e) => { Touch(e.OldFullPath); Touch(e.FullPath); };
                w.Error += (_, e) => { Log.Warn("watcher buffer overflow, full rescan", e.GetException()); Post(scan.Reconcile); };
                w.EnableRaisingEvents = true;
                Folders.Add((scan, w));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { Log.Warn("folder not watched: " + Fmt.Err(e)); }
        }
        Log.Info($"watching {Folders.Count} folder(s), {index.Items.Count} item(s)");
    }

    /// <summary>Stop the watcher, cancel hashing, wait for the worker up to 5 s, write state.json.</summary>
    public static void Stop()
    {
        if (thread is null) return;
        foreach (var (_, w) in Folders.ToList()) w.Dispose();
        Stopping.Cancel();
        Jobs.CompleteAdding();
        var joined = thread.Join(TimeSpan.FromSeconds(5));
        thread = null;
        try { Index.Save(Index.StatePath, joined ? index.Snapshot() : Current); }
        catch (Exception e) { Log.Error("state.json not saved on exit: " + Fmt.Err(e)); }
    }

    public static void Post(Action job)
    {
        try { Jobs.Add(job); }
        catch (InvalidOperationException) { } // stopping
    }

    /// <summary>Runs on the worker and hands the result back.</summary>
    public static Task<T> Ask<T>(Func<T> f)
    {
        var t = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => { try { t.SetResult(f()); } catch (Exception e) { t.SetException(e); } });
        return t.Task;
    }

    static void Loop()
    {
        nextTick = DateTime.UtcNow.AddSeconds(30);
        while (!Stopping.IsCancellationRequested)
        {
            try
            {
                if (Jobs.TryTake(out var job, 1000, Stopping.Token)) job();
                var now = DateTime.UtcNow;
                foreach (var (scan, _) in Folders) scan.ProcessPending(now);
                if (index.Dirty) Changed_();
                if (now >= detectDue) { detectDue = DateTime.MaxValue; Detect(); if (index.Dirty) Changed_(); }
                if (now >= nextTick) { nextTick = now.AddMinutes(15); Tick(now); if (index.Dirty) Changed_(); }
                if (now >= saveDue) Save();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e) { Log.Error("worker job failed: " + Fmt.Err(e)); } // one bad file never stops the worker
        }
    }

    /// <summary>Publish a snapshot; detectors and the state write follow 2 s after the last change.</summary>
    static void Changed_()
    {
        index.Dirty = false;
        var soon = DateTime.UtcNow.AddSeconds(2);
        if (saveDue > soon) saveDue = soon;
        if (detectDue > soon) detectDue = soon;
        var s = index.Snapshot();
        ui?.BeginInvoke(() => { Current = s; Changed?.Invoke(); });
    }

    static void Save()
    {
        saveDue = DateTime.MaxValue;
        try { Index.Save(Index.StatePath, index.Snapshot()); }
        catch (Exception e) { Log.Error("state.json not saved: " + Fmt.Err(e)); saveDue = DateTime.UtcNow.AddSeconds(30); }
    }

    /// <summary>Duplicates first (a hash is proof), then installers, then extracted zips. Installer and zip checks are cached per tick.</summary>
    static void Detect()
    {
        var now = DateTime.UtcNow;
        var dups = Duplicates.Find(index, Stopping.Token);
        var rules = Settings.Current.SiteRules.ToList();
        var moves = new List<(Item Item, string Folder)>();
        foreach (var i in index.Items.Values.ToList())
        {
            var s = i.Kept ? null : dups.GetValueOrDefault(i.Key);
            var archive = Archives.Supported(i.Path);
            if (s is null && !i.Kept && !i.IsDir && (i.Kind == Kind.Installer || archive))
            {
                // an archive is looked at again when the folder it was extracted to changes, or at the 15 min tick for deeper changes
                var folder = archive ? index.ByPath(Path.Combine(Path.GetDirectoryName(i.Path)!, Archives.Stem(i.Path)))?.Stamp : null;
                var mark = $"{i.Stamp}/{generation}/{folder}";
                if (Checked.GetValueOrDefault(i.Key) == mark) s = i.Suggestion is { Type: "installer" or "archive" } ? i.Suggestion : null;
                else
                {
                    Stopping.Token.ThrowIfCancellationRequested();
                    s = archive ? Archive(i) : Installers.Check(i, uninstall);
                    Checked[i.Key] = mark;
                }
            }
            var item = i;
            if (s is null && !i.Kept && !i.Ruled && SiteRule.First(rules, i) is { } r) // v1.0 site rules: only where no detector spoke
            {
                if (r.Action == "clean") s = new("site", "likely", r.Domain);
                else if (!r.Auto)
                {
                    if (r.Action == "move" || i.TtlUtc is null)
                        s = new("rule", "exact", r.Domain, Why: r.Action, Arg: r.Action == "move" ? r.Folder : r.Days.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                else if (r.Action == "ttl")
                {
                    item = i with { TtlUtc = i.TtlUtc ?? i.FirstSeenUtc + r.Days * Folder.Day, Ruled = true }; // a lifetime only deletes through its own rule
                    if (i.TtlUtc is null) Index.Append(new(now, "ttl", i.Path, i.Size, $"{r.Days}d", "rule:site"));
                }
                else if (r.Folder is { } dir) moves.Add((i, dir));
            }
            if (s is not null && i.Suggestion is { } old && s.Same(old)) s = old; // keeps the time it was first found
            else if (s is not null) s = s with { FoundUtc = now };
            item = item with { Suggestion = s };
            if (item != i) index.Set(item);
        }
        // under --ttl-minutes (Debug sandbox) the start delay is 10 s, like a day is a minute
        if (moves.Count > 0 && !Paused && now - StartedUtc >= (Folder.TtlMinutes ? TimeSpan.FromSeconds(10) : Rules.StartDelay))
            ui?.BeginInvoke(() => Actions.RunRuleMoves(moves));
    }

    static Suggestion? Archive(Item i) =>
        Archives.Check(i.Path) is { Percent: >= Archives.Threshold } c ? new("archive", c.Percent == 100 ? "exact" : "likely", c.Where, Percent: c.Percent) : null;

    /// <summary>Every 15 min (by the UTC clock, so sleep doesn't break lifetimes): fresh Uninstall list, folder sizes, detectors, rules, reminder.</summary>
    static void Tick(DateTime now)
    {
        generation++;
        uninstall = UninstallEntries.List();
        foreach (var (scan, _) in Folders) scan.RefreshFolderSizes();
        Detect();
        var s = index.Snapshot();
        var plan = Rules.AutoPlan(s.Items, now, StartedUtc, Paused, Settings.Current);
        ui?.BeginInvoke(() =>
        {
            if (plan.Count > 0) Actions.RunAuto(plan);
            Reminder.Check(s);
        });
    }

    // ---- requests from the UI and the --ttl pipe ----

    public static void MarkSeen()
    {
        if (Args.Current.Demo) ShowDemo(Current with { LastTriageUtc = DateTime.UtcNow });
        else Post(() => { index.LastTriageUtc = DateTime.UtcNow; index.Dirty = true; });
    }

    /// <summary>Site rules changed: look at everything again soon.</summary>
    public static void Redetect() => Post(() => detectDue = DateTime.UtcNow);

    /// <summary>A site rule was applied by hand or can't be (its folder is gone): never offered again for this file.</summary>
    public static void MarkRuled(string key)
    {
        if (Args.Current.Demo) { ShowDemo(Current with { Items = [.. Current.Items.Select(i => i.Key == key ? i with { Ruled = true, Suggestion = null } : i)] }); return; }
        Post(() => { if (index.Items.GetValueOrDefault(key) is { } i) index.Set(i with { Ruled = true, Suggestion = i.Suggestion is { Type: "rule" } ? null : i.Suggestion }); });
    }

    /// <summary>After a recycle or move: forget at once instead of waiting for the watcher.</summary>
    public static void Forget(string key)
    {
        if (Args.Current.Demo) ShowDemo(Current with { Items = [.. Current.Items.Where(i => i.Key != key)] });
        else Post(() => index.Remove(key));
    }

    /// <summary>Lifetime, keep or clear for items by key (the window).</summary>
    public static void Apply(IEnumerable<string> keys, TtlCommand cmd)
    {
        var list = keys.ToList();
        if (Args.Current.Demo) { ShowDemo(Current with { Items = [.. Current.Items.Select(i => list.Contains(i.Key) ? cmd.Apply(i, DateTime.UtcNow, Folder.Day) : i)] }); return; }
        Post(() => { foreach (var k in list) if (index.Items.GetValueOrDefault(k) is { } i) Apply(i, cmd); });
    }

    /// <summary>The context menu's batch: paths outside the watched folder are skipped and counted (lifetime works only there).</summary>
    public static Task<(int Done, int Outside)> ApplyPaths(IReadOnlyList<string> paths, TtlCommand cmd) => Ask(() =>
    {
        int done = 0, outside = 0;
        foreach (var p in paths)
        {
            var scan = Folders.Select(f => f.Scan).FirstOrDefault(f => Scanner.IsTopLevel(f.Folder, p));
            if (scan?.Upsert(p, force: true) is { } key && index.Items.GetValueOrDefault(key) is { } i) { Apply(i, cmd); done++; }
            else outside++;
        }
        return (done, outside);
    });

    static void Apply(Item i, TtlCommand cmd)
    {
        index.Set(cmd.Apply(i, DateTime.UtcNow, Folder.Day));
        Index.Append(new(DateTime.UtcNow, cmd.Op == "keep" ? "keep" : "ttl", i.Path, i.Size, cmd.Op == "keep" ? "" : cmd.ToString(), "user"));
    }
}
