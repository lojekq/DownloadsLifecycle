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
    static Scanner? scanner;
    static IReadOnlyList<UninstallEntry> uninstall = [];
    static int generation;
    static DateTime saveDue = DateTime.MaxValue, detectDue = DateTime.MaxValue, nextTick;
    static Thread? thread;
    static FileSystemWatcher? watcher;
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
        catch (Exception e) { Log.Error("state.json not readable", e); } // locked: start empty this time
        FirstRun = saved is null;
        index = Index.From(saved ?? new([], DateTime.UtcNow)); // first run: what is already there is not "new"
        index.Dirty = FirstRun;
        Current = index.Snapshot();
        scanner = new Scanner(index, Folder.Path);
        Index.RotateJournal(DateTime.UtcNow);

        thread = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "worker" };
        thread.Start();
        Post(() =>
        {
            uninstall = UninstallEntries.List();
            scanner.Reconcile();
            watcher = new FileSystemWatcher(Folder.Path)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
                InternalBufferSize = 64 * 1024,
            };
            watcher.Created += (_, e) => Touch(e.FullPath);
            watcher.Changed += (_, e) => Touch(e.FullPath);
            watcher.Deleted += (_, e) => Touch(e.FullPath);
            watcher.Renamed += (_, e) => { Touch(e.OldFullPath); Touch(e.FullPath); };
            watcher.Error += (_, e) => { Log.Warn("watcher buffer overflow, full rescan", e.GetException()); Post(scanner.Reconcile); };
            watcher.EnableRaisingEvents = true;
            Log.Info($"watching, {index.Items.Count} item(s)");
        });
    }

    /// <summary>Stop the watcher, cancel hashing, wait for the worker up to 5 s, write state.json.</summary>
    public static void Stop()
    {
        if (thread is null) return;
        watcher?.Dispose();
        Stopping.Cancel();
        Jobs.CompleteAdding();
        var joined = thread.Join(TimeSpan.FromSeconds(5));
        thread = null;
        try { Index.Save(Index.StatePath, joined ? index.Snapshot() : Current); }
        catch (Exception e) { Log.Error("state.json not saved on exit", e); }
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

    static void Touch(string path) => Post(() => scanner!.Touch(path, DateTime.UtcNow));

    static void Loop()
    {
        nextTick = DateTime.UtcNow.AddSeconds(30);
        while (!Stopping.IsCancellationRequested)
        {
            try
            {
                if (Jobs.TryTake(out var job, 1000, Stopping.Token)) job();
                var now = DateTime.UtcNow;
                scanner?.ProcessPending(now);
                if (index.Dirty) Changed_();
                if (now >= detectDue) { detectDue = DateTime.MaxValue; Detect(); if (index.Dirty) Changed_(); }
                if (now >= nextTick) { nextTick = now.AddMinutes(15); Tick(now); if (index.Dirty) Changed_(); }
                if (now >= saveDue) Save();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e) { Log.Error("worker job failed", e); } // one bad file never stops the worker
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
        catch (Exception e) { Log.Error("state.json not saved", e); saveDue = DateTime.UtcNow.AddSeconds(30); }
    }

    /// <summary>Duplicates first (a hash is proof), then installers, then extracted zips. Installer and zip checks are cached per tick.</summary>
    static void Detect()
    {
        var dups = Duplicates.Find(index, Stopping.Token);
        foreach (var i in index.Items.Values.ToList())
        {
            var s = i.Kept ? null : dups.GetValueOrDefault(i.Key);
            var zip = Path.GetExtension(i.Path).Equals(".zip", StringComparison.OrdinalIgnoreCase);
            if (s is null && !i.Kept && !i.IsDir && (i.Kind == Kind.Installer || zip))
            {
                var mark = $"{i.Stamp}/{generation}";
                if (Checked.GetValueOrDefault(i.Key) == mark) s = i.Suggestion is { Type: not "duplicate" } ? i.Suggestion : null;
                else
                {
                    Stopping.Token.ThrowIfCancellationRequested();
                    s = zip ? Archive(i) : Installers.Check(i, uninstall);
                    Checked[i.Key] = mark;
                }
            }
            if (s != i.Suggestion) index.Set(i with { Suggestion = s });
        }
    }

    static Suggestion? Archive(Item i) =>
        ZipCoverage.Check(i.Path) is { Percent: >= ZipCoverage.Threshold } c ? new("archive", c.Percent == 100 ? "exact" : "likely", c.Where, Percent: c.Percent) : null;

    /// <summary>Every 15 min (by the UTC clock, so sleep doesn't break lifetimes): fresh Uninstall list, folder sizes, detectors, rules, reminder.</summary>
    static void Tick(DateTime now)
    {
        generation++;
        uninstall = UninstallEntries.List();
        scanner?.RefreshFolderSizes();
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
            if (scanner?.Upsert(p, force: true) is { } key && index.Items.GetValueOrDefault(key) is { } i) { Apply(i, cmd); done++; }
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
