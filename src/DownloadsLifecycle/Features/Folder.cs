using System.IO;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Storage.FileSystem;
using DownloadsLifecycle.Shell;

namespace DownloadsLifecycle.Features;

/// <summary>
/// The watched folder. Release: the real Downloads (FOLDERID_Downloads). Debug: C:\Demo\Downloads or --watch-dir &lt;path&gt;,
/// so a Debug build never touches the real Downloads (00-shared §9.4). --ttl-minutes (Debug, only under C:\Demo) turns days into minutes.
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

    public static bool Contains(string p) => Scanner.IsTopLevel(Path, p);

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
