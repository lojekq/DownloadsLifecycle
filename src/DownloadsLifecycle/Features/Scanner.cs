using System.IO;

namespace DownloadsLifecycle.Features;

/// <summary>
/// Keeps the index in step with the top level of one folder (no recursion). Watcher events only mark a path;
/// a path is looked at once it has been quiet for 5 s. Runs on the worker only.
/// </summary>
public sealed class Scanner(Index index, string folder)
{
    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(5);
    readonly Dictionary<string, DateTime> pending = new(StringComparer.OrdinalIgnoreCase); // path → last event (UTC)

    public string Folder { get; } = folder;

    public void Touch(string path, DateTime nowUtc) => pending[path] = nowUtc;

    /// <summary>Full sync: at start and after a watcher overflow.</summary>
    public void Reconcile()
    {
        var seen = new HashSet<string>();
        foreach (var p in Directory.EnumerateFileSystemEntries(Folder))
            if (Upsert(p) is { } key) seen.Add(key);
        foreach (var gone in index.Items.Values.Where(i => IsTopLevel(Folder, i.Path) && !seen.Contains(i.Key) && !pending.ContainsKey(i.Path)).Select(i => i.Key).ToList())
            index.Remove(gone);
    }

    /// <summary>Paths quiet for 5 s. Existing ones first, so a renamed file finds its item before the old path is dropped.</summary>
    public void ProcessPending(DateTime nowUtc)
    {
        var due = pending.Where(p => nowUtc - p.Value >= Quiet).Select(p => p.Key).ToList();
        foreach (var p in due) pending.Remove(p);
        foreach (var p in due.OrderBy(p => Path.Exists(p) ? 0 : 1))
            if (Upsert(p) is null && !Path.Exists(p) && index.ByPath(p) is { } gone)
                index.Remove(gone.Key); // moved out of the folder or deleted: its lifetime goes with it, on purpose (08 §5)
    }

    /// <summary>
    /// Adds or refreshes one top-level entry; returns its key, or null when it's ignored, gone or (new and) not ready yet.
    /// <paramref name="force"/>: the user pointed at it (context menu), so don't wait for the 5 s quiet.
    /// </summary>
    public string? Upsert(string path, bool force = false)
    {
        if (!IsTopLevel(Folder, path)) return null;
        FileSystemInfo fi = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        if (!fi.Exists || Ignored(fi)) return null;
        if (Features.Folder.Key(path) is not { } key) return null;

        if (index.ByPath(path) is { } replaced && replaced.Key != key) index.Remove(replaced.Key); // same name, another file

        var dir = fi is DirectoryInfo;
        index.Items.TryGetValue(key, out var existing);
        if (existing is null && !dir && !force && !Ready((FileInfo)fi)) { pending[path] = DateTime.UtcNow; return null; }
        var size = dir ? (existing is { Size: > 0 } && existing.MtimeUtc == fi.LastWriteTimeUtc ? existing.Size : DirSize(path)) : ((FileInfo)fi).Length;
        var item = (existing ?? new Item
        {
            Key = key, Path = path, FirstSeenUtc = DateTime.UtcNow,
            Src = dir ? null : ZoneReader.Read(path), // taken once: survives "Unblock" and lost streams
        }) with
        {
            Path = path, IsDir = dir, Size = size, MtimeUtc = fi.LastWriteTimeUtc, CreatedUtc = fi.CreationTimeUtc, Kind = Item.KindOf(path, dir),
        };
        if (existing is not null && existing.Stamp != item.Stamp) item = item with { Suggestion = null }; // contents changed: detect again
        index.Set(item);
        return key;
    }

    /// <summary>Folder sizes go stale (the watcher doesn't look inside); the 15 min tick refreshes them.</summary>
    public void RefreshFolderSizes()
    {
        foreach (var i in index.Items.Values.Where(i => i.IsDir && IsTopLevel(Folder, i.Path)).ToList())
            if (Directory.Exists(i.Path)) index.Set(i with { Size = DirSize(i.Path) });
    }

    public static bool IsTopLevel(string folder, string path) =>
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(path).TrimEnd('\\')), Path.GetFullPath(folder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Download leftovers, hidden/system files (desktop.ini) and links (reparse points) are never indexed or touched.</summary>
    public static bool Ignored(FileSystemInfo fi) =>
        (fi.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System)) != 0 || (fi is FileInfo && IsPartial(fi.Name));

    /// <summary>Chrome/Edge .crdownload ("Unconfirmed 123.crdownload" too), Firefox .part, Opera .opdownload, our own .dltmp.</summary>
    public static bool IsPartial(string name) =>
        Path.GetExtension(name).ToLowerInvariant() is ".crdownload" or ".part" or ".partial" or ".opdownload" or ".tmp" or ".download" or ".dltmp";

    /// <summary>A new file counts once it hasn't changed for 5 s and nobody writes it (it opens with FileShare.Read).</summary>
    public static bool Ready(FileInfo f)
    {
        if (DateTime.UtcNow - f.LastWriteTimeUtc < Quiet) return false;
        try { using var _ = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    static long DirSize(string path)
    {
        try
        {
            return new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions
            { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).Sum(f => f.Length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }
}
