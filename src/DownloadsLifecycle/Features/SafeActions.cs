using System.IO;
using System.Runtime.InteropServices;
using DownloadsLifecycle.Shell;

namespace DownloadsLifecycle.Features;

public enum Verdict { Ok, Gone, Outside, Changed, Kept, NotFixed, LongPath, InUse, TooYoung, TooBig }

/// <summary>What is true about the file right now, next to what was true when the decision was made.</summary>
public sealed record Facts(bool Exists, bool Inside, bool SameFile, bool Unchanged, bool Kept, bool FixedDrive, bool LongPath, bool InUse, long Size, TimeSpan Age)
{
    /// <summary>Looks at the disk again (08 §8: path inside the folder, same key, size and mtime as at the decision).</summary>
    public static Facts Observe(Item i, DateTime nowUtc)
    {
        FileSystemInfo fi = i.IsDir ? new DirectoryInfo(i.Path) : new FileInfo(i.Path);
        if (!fi.Exists) return new(false, false, false, false, i.Kept, false, false, false, i.Size, TimeSpan.Zero);
        var unchanged = fi.LastWriteTimeUtc == i.MtimeUtc && (i.IsDir || ((FileInfo)fi).Length == i.Size);
        bool fixedDrive;
        try { fixedDrive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(i.Path))!).DriveType == DriveType.Fixed; }
        catch (ArgumentException) { fixedDrive = false; } // UNC
        return new(true, Folder.Contains(i.Path), Folder.Key(i.Path) == i.Key, unchanged, i.Kept, fixedDrive, i.Path.Length > 259,
            !i.IsDir && (nowUtc - fi.LastWriteTimeUtc < Scanner.Quiet || !Exclusive(i.Path)), i.Size, nowUtc - i.CreatedUtc);
    }

    static bool Exclusive(string path)
    {
        try { using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}

/// <summary>"May we act on this file now?" Pure, so every rule is tested (08 §9). Auto adds the age and size limits.</summary>
public static class SafetyGate
{
    public static Verdict Check(Facts f, bool auto, int graceHours, long autoMaxBytes) =>
        !f.Exists ? Verdict.Gone
        : !f.Inside ? Verdict.Outside
        : !f.SameFile || !f.Unchanged ? Verdict.Changed
        : auto && f.Kept ? Verdict.Kept
        : !f.FixedDrive ? Verdict.NotFixed     // removable or network: the Recycle Bin may not take it
        : f.LongPath ? Verdict.LongPath        // SHFileOperation and long paths: not verified, so we don't try
        : f.InUse ? Verdict.InUse              // still written or opened by another program: later
        : auto && f.Age < TimeSpan.FromHours(graceHours) ? Verdict.TooYoung
        : auto && f.Size > autoMaxBytes ? Verdict.TooBig
        : Verdict.Ok;
}

/// <summary>The Recycle Bin via SHFileOperationW: one absolute path per call, never permanent.</summary>
public static class Recycler
{
    const uint FO_DELETE = 3;
    const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_WANTNUKEWARNING = 0x4000;

    // ponytail: x64 layout (natural alignment); 32-bit Windows packs this struct to 1 byte, and we only ship win-x64
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCTW
    {
        public nint hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public nint hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHFileOperationW(ref SHFILEOPSTRUCTW op);

    /// <summary>pFrom: an absolute path ending in two nulls (one null would make Windows read past the string as more paths). No wildcards.</summary>
    public static string PFrom(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("not an absolute path");
        if (path.IndexOfAny(['*', '?', '\0']) >= 0) throw new ArgumentException("wildcards are not allowed");
        return path + "\0\0";
    }

    /// <summary>True when the item is in the Recycle Bin and gone from its place. Runs on the UI (STA) thread; owner = the main window.</summary>
    public static bool Recycle(string path, nint owner)
    {
        if (Args.Current.Demo) return true;
        var op = new SHFILEOPSTRUCTW
        {
            hwnd = owner, wFunc = FO_DELETE, pFrom = PFrom(path),
            // FOF_WANTNUKEWARNING: if it can't go to the Recycle Bin, Windows asks instead of deleting it for good
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_WANTNUKEWARNING,
        };
        var rc = SHFileOperationW(ref op);
        if (rc != 0) Log.Warn($"SHFileOperation returned 0x{rc:X}");
        return rc == 0 && op.fAnyOperationsAborted == 0 && !Path.Exists(path);
    }
}

/// <summary>Moves without overwriting. Across volumes: copy to *.dltmp, verify, rename, and only then the source goes to the Recycle Bin.</summary>
public static class Mover
{
    /// <summary>"a.pdf" taken → "a (2).pdf", "a (3).pdf"…</summary>
    public static string UniqueName(string dir, string name)
    {
        var target = Path.Combine(dir, name);
        if (!Path.Exists(target)) return target;
        var ext = Path.GetExtension(name);
        if (ext.Contains(' ')) ext = "";
        var stem = name[..^ext.Length];
        for (var n = 2; ; n++)
            if (Path.Combine(dir, $"{stem} ({n}){ext}") is var t && !Path.Exists(t)) return t;
    }

    /// <summary>Returns the new path. Throws on failure; the source then stays where it was.</summary>
    public static string Move(string from, string toDir, bool isDir, nint owner)
    {
        var target = UniqueName(toDir, Path.GetFileName(from));
        if (Args.Current.Demo) return target;
        var sameVolume = string.Equals(Path.GetPathRoot(Path.GetFullPath(from)), Path.GetPathRoot(Path.GetFullPath(toDir)), StringComparison.OrdinalIgnoreCase);
        if (sameVolume)
        {
            if (isDir) Directory.Move(from, target); else File.Move(from, target);
            return target;
        }
        // ponytail: folders move within one volume only; copying a tree across volumes safely is v1.0 work if anyone asks
        if (isDir) throw new NotSupportedException("folder to another drive");
        var tmp = target + ".dltmp";
        try
        {
            File.Copy(from, tmp);
            var src = new FileInfo(from);
            if (new FileInfo(tmp).Length != src.Length) throw new IOException("size differs after copy");
            if (src.Length < 1L << 30 && Duplicates.Hash(tmp, default) != Duplicates.Hash(from, default)) throw new IOException("hash differs after copy");
            File.SetLastWriteTimeUtc(tmp, src.LastWriteTimeUtc);
            File.Move(tmp, target);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
        if (!Recycler.Recycle(from, owner)) throw new IOException($"copied to {target}, but the original could not go to the Recycle Bin");
        return target;
    }
}

/// <summary>
/// v1.0 "Restore": the Shell's Recycle Bin folder (Shell.Application, NameSpace 10) and its "undelete" verb, on the UI (STA) thread.
/// Picks the item deleted from that folder with that name, closest to the journal time. If the Shell won't, "Show in Recycle Bin" is still there.
/// </summary>
public static class RecycleBin
{
    public static bool Restore(string path, DateTime deletedUtc)
    {
        if (Args.Current.Demo) return true;
        return Walk(items =>
        {
            var best = items.Where(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase))
                .OrderBy(i => Math.Abs((i.DeletedUtc - deletedUtc).TotalSeconds)).Select(i => i.Item).FirstOrDefault();
            if (best is null) return false;
            best.InvokeVerb("undelete");
            for (var n = 0; n < 30 && !Path.Exists(path); n++) Thread.Sleep(100); // the Shell may finish a moment later
            return Path.Exists(path);
        });
    }

    /// <summary>Original path and deletion time of everything in the Recycle Bin (all drives); null when the Shell would not say.</summary>
    public static List<(string Path, DateTime DeletedUtc)>? Contents() => Walk(items => items.Select(i => (i.Path, i.DeletedUtc)).ToList());

    /// <summary>Is this recycle still in the Bin? Same original path, deleted within two minutes of the journal entry.</summary>
    public static bool Holds(IEnumerable<(string Path, DateTime DeletedUtc)> bin, string path, DateTime deletedUtc) =>
        bin.Any(b => string.Equals(b.Path, path, StringComparison.OrdinalIgnoreCase) && Math.Abs((b.DeletedUtc - deletedUtc).TotalMinutes) < 2);

    static T? Walk<T>(Func<IEnumerable<(dynamic Item, string Path, DateTime DeletedUtc)>, T> use)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        try
        {
            IEnumerable<(dynamic Item, string Path, DateTime DeletedUtc)> items = Items(shell.NameSpace(10).Items());
            return use(items);
        }
        catch (Exception e) when (e is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException)
        {
            Log.Warn("recycle bin: " + Fmt.Err(e));
            return default;
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    static IEnumerable<(dynamic Item, string Path, DateTime DeletedUtc)> Items(dynamic items)
    {
        foreach (dynamic item in items)
        {
            if (item.ExtendedProperty("System.Recycle.DeletedFrom") is not string from) continue;
            string shown = item.Name, ext = Path.GetExtension((string)item.Path); // Name lacks the extension when Explorer hides them; the $R… path keeps it
            var name = string.Equals(Path.GetExtension(shown), ext, StringComparison.OrdinalIgnoreCase) ? shown : shown + ext;
            // the Shell gives the FILETIME as is: UTC, but Kind Unspecified (ToUniversalTime would shift it by the time zone)
            var when = item.ExtendedProperty("System.Recycle.DateDeleted") is DateTime d
                ? d.Kind == DateTimeKind.Local ? d.ToUniversalTime() : DateTime.SpecifyKind(d, DateTimeKind.Utc) : DateTime.MinValue;
            yield return (item, Path.Combine(from, name), when);
        }
    }
}
