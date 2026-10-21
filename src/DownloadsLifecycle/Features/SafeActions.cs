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
