using System.IO;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Storage.FileSystem;
using DownloadsLifecycle.Shell;

namespace DownloadsLifecycle.Features;

/// <summary>
/// The watched folders. The main one: Release the real Downloads (FOLDERID_Downloads), Debug C:\Demo\Downloads or --watch-dir &lt;path&gt;,
/// so a Debug build never touches the real Downloads (00-shared §9.4). Extra ones (Telegram Desktop…) come from Settings.
/// --ttl-minutes (Debug, only under C:\Demo) turns days into minutes.
/// </summary>
public static class Folder
{
    public const string Sandbox = @"C:\Demo\Downloads";

    static string? path;
    public static string Path => path ??= Resolve(Args.Current.Unknown);

    /// <summary>A lifetime "day" is a minute: Debug only, with the folder under C:\Demo, for the manual TTL check.</summary>
    public static bool TtlMinutes =>
#if DEBUG
        Args.Current.Unknown.Contains("--ttl-minutes") && Paths.IsUnder(@"C:\Demo", Path);
#else
        false;
#endif

    public static TimeSpan Day => TtlMinutes ? TimeSpan.FromMinutes(1) : TimeSpan.FromDays(1);

    internal static string Resolve(IReadOnlyList<string> unknown)
    {
        if (Args.Current.Demo) return @"C:\Users\demo\Downloads"; // shown in screenshots: never a real user name
#if DEBUG || SANDBOX // SANDBOX: Release single-file for installer tests (-p:Sandbox=true)
        var i = unknown.ToList().IndexOf("--watch-dir");
        var dir = i >= 0 && i + 1 < unknown.Count ? System.IO.Path.GetFullPath(unknown[i + 1]) : Sandbox;
        Directory.CreateDirectory(dir);
        return dir;
#else
        return Paths.Downloads;
#endif
    }

    /// <summary>The main folder first, then the extra ones that are still allowed and exist.</summary>
    public static IReadOnlyList<string> All =>
        [Path, .. Settings.Current.ExtraFolders.Where(f => Allowed(f) is null && Directory.Exists(f)).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Why a folder can't be watched, or null. Never a drive root, a folder that holds the main one, or Windows; Debug only under C:\Demo.</summary>
    public static string? Allowed(string dir)
    {
        if (!System.IO.Path.IsPathFullyQualified(dir)) return "Folder.Bad";
        var full = System.IO.Path.GetFullPath(dir).TrimEnd('\\');
        if (full.Length <= 3 || Paths.IsUnder(full, Path) || Paths.IsUnder(Environment.GetFolderPath(Environment.SpecialFolder.Windows), full)) return "Folder.Bad";
#if DEBUG
        if (!Args.Current.Demo && !Paths.IsUnder(@"C:\Demo", full)) return "Folder.DebugOnlyDemo";
#endif
        return null;
    }

    /// <summary>The watched folder that holds <paramref name="p"/> at its top level, or null.</summary>
    public static string? RootOf(string p) => All.FirstOrDefault(f => Scanner.IsTopLevel(f, p));

    public static bool Contains(string p) => RootOf(p) is not null;

    /// <summary>
    /// VolumeSerial + 128-bit FileId (FILE_ID_INFO): survives renames and moves within the volume. Opened with FILE_READ_ATTRIBUTES only,
    /// so it works on files another program holds open and on folders; reparse points are opened themselves, never followed. Null if gone.
    /// </summary>
    public static unsafe string? Key(string p)
    {
        const uint FILE_READ_ATTRIBUTES = 0x80;
        using SafeFileHandle h = PInvoke.CreateFile(p, FILE_READ_ATTRIBUTES,
            FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE, null,
            FILE_CREATION_DISPOSITION.OPEN_EXISTING,
            FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OPEN_REPARSE_POINT, null);
        if (h.IsInvalid) return null;
        FILE_ID_INFO info;
        if (!PInvoke.GetFileInformationByHandleEx(h, FILE_INFO_BY_HANDLE_CLASS.FileIdInfo, new Span<byte>(&info, sizeof(FILE_ID_INFO)))) return null;
        return $"{info.VolumeSerialNumber:X16}-{Convert.ToHexString(new ReadOnlySpan<byte>(info.FileId.Identifier.Value, 16))}";
    }
}
