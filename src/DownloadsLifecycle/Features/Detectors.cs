using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.ApplicationInstallationAndServicing;
using DownloadsLifecycle.Shell;

namespace DownloadsLifecycle.Features;

/// <summary>"a (1).pdf" (Chrome, Edge), "a(1).pdf" (Firefox), "a - Copy.pdf", "a - копия (2).pdf" (Explorer), "a.tar (1).gz" → the original name.</summary>
public static partial class CopySuffix
{
    [GeneratedRegex(@"^(?<base>.+?)(?:\s?\(\d{1,3}\)|\s[-–—]\s(?:copy|копия)(?:\s\(\d{1,3}\))?)$", RegexOptions.IgnoreCase)]
    private static partial Regex Rx();

    /// <summary>Null when the name has no copy suffix.</summary>
    public static string? Original(string name)
    {
        var ext = Path.GetExtension(name);
        if (ext.Contains(' ') || ext.Contains('(')) ext = ""; // "report v1.2 (1)": no real extension
        var m = Rx().Match(name[..^ext.Length]);
        return m.Success ? m.Groups["base"].Value + ext : null;
    }
}

/// <summary>Same size, then the same SHA-256. One copy of the contents always stays.</summary>
public static class Duplicates
{
    /// <summary>The copy that stays: one the user kept, else one without a copy suffix, else the oldest.</summary>
    public static Item Keeper(IReadOnlyList<Item> same) =>
        same.Where(i => i.Kept).MinBy(i => i.CreatedUtc)
        ?? same.Where(i => CopySuffix.Original(i.Name) is null).MinBy(i => i.CreatedUtc)
        ?? same.MinBy(i => i.CreatedUtc)!;

    /// <summary>Files with a size twin, hashed (cached by size+mtime). Returns key → suggestion for every non-keeper.</summary>
    public static Dictionary<string, Suggestion> Find(Index index, CancellationToken stop)
    {
        var result = new Dictionary<string, Suggestion>();
        var twins = index.Items.Values.Where(i => !i.IsDir && i.Size > 0).GroupBy(i => i.Size).Where(g => g.Count() > 1).SelectMany(g => g).ToList();
        foreach (var i in twins)
        {
            if (i.HashStamp == i.Stamp && i.Sha256 is not null) continue;
            stop.ThrowIfCancellationRequested();
            if (Hash(i.Path, stop) is { } h) index.Set(i with { Sha256 = h, HashStamp = i.Stamp });
        }
        foreach (var g in twins.Select(i => index.Items.GetValueOrDefault(i.Key)).OfType<Item>()
                     .Where(i => i.Sha256 is not null && i.HashStamp == i.Stamp).GroupBy(i => i.Sha256).Where(g => g.Count() > 1))
        {
            var keep = Keeper([.. g]);
            foreach (var i in g.Where(i => i.Key != keep.Key && !i.Kept)) result[i.Key] = new("duplicate", "exact", keep.Name);
        }
        return result;
    }

    public static string? Hash(string path, CancellationToken stop)
    {
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buf = new byte[1 << 20];
            for (int n; (n = f.Read(buf)) > 0;)
            {
                stop.ThrowIfCancellationRequested();
                sha.AppendData(buf, 0, n);
            }
            return Convert.ToHexStringLower(sha.GetHashAndReset());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}

/// <summary>An exe's version resource, the fields the matcher looks at.</summary>
public sealed record ExeInfo(string? ProductName, string? Company, string? Description, string? ProductVersion, string? FileVersion)
{
    public static ExeInfo Read(string path)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            return new(v.ProductName, v.CompanyName, v.FileDescription, v.ProductVersion, v.FileVersion);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FileNotFoundException) { return new(null, null, null, null, null); }
    }
}

public sealed record ExeMatch(UninstallEntry Entry, bool Exact, string Why); // Why: name | version

/// <summary>
/// EXE installers against the Uninstall keys: name tokens (file name, product, description, company) and versions.
/// Exact = the whole program name and the version match; likely = the whole name, or the version plus the publisher or a name token.
/// No match = no offer: an unknown exe may be a portable program (08 §2).
/// </summary>
public static partial class ExeMatcher
{
    static readonly HashSet<string> Noise =
    [
        "setup", "installer", "install", "win", "windows", "amd", "arm", "bit", "user", "system", "full", "offline", "online", "latest",
        "release", "stable", "the", "and", "for", "of", "corporation", "corp", "inc", "ltd", "llc", "gmbh", "co", "edition", "update",
        "version", "international", "dch", "whql", "msvc", "exe", "msi", "application", "app",
    ];

    [GeneratedRegex(@"[A-Z]+(?![a-z])|[A-Z]?[a-z]+|\d+")] private static partial Regex Words();
    [GeneratedRegex(@"\d+(?:\.\d+)+")] private static partial Regex VersionRx();

    /// <summary>"VSCodeUserSetup-x64" → {vs, code}; numbers, single letters and noise words dropped.</summary>
    public static HashSet<string> Tokens(string? s) =>
        s is null ? [] : Words().Matches(s).Select(m => m.Value.ToLowerInvariant())
            .Where(t => t.Length > 1 && !char.IsDigit(t[0]) && !Noise.Contains(t)).ToHashSet();

    /// <summary>"1.95.3.0" → "1.95.3": versions compare without trailing zero parts.</summary>
    public static HashSet<string> Versions(string? s) =>
        s is null ? [] : VersionRx().Matches(s).Select(m =>
        {
            var parts = m.Value.Split('.').Select(p => p.TrimStart('0') is "" ? "0" : p.TrimStart('0')).ToList();
            while (parts.Count > 1 && parts[^1] == "0") parts.RemoveAt(parts.Count - 1);
            return string.Join('.', parts);
        }).ToHashSet();

    public static ExeMatch? Match(string fileName, ExeInfo info, IEnumerable<UninstallEntry> entries)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var own = Tokens(stem).Union(Tokens(info.ProductName)).Union(Tokens(info.Description)).ToHashSet(); // the program's own name words
        var company = Tokens(info.Company);
        var versions = Versions(stem).Union(Versions(info.ProductVersion)).Union(Versions(info.FileVersion)).ToHashSet();

        ExeMatch? best = null;
        (int, int, int) bestRank = default;
        foreach (var e in entries)
        {
            if (e.DisplayName is null || e.SystemComponent) continue;
            var name = Tokens(e.DisplayName);
            if (name.Count == 0) continue;
            var hits = name.Count(t => own.Contains(t) || company.Contains(t));
            var fullName = hits == name.Count && name.Any(own.Contains); // company words alone never name a program
            var sameVersion = versions.Overlaps(Versions(e.DisplayVersion).Union(Versions(e.DisplayName)));
            var samePublisher = company.Overlaps(Tokens(e.Publisher));
            var exact = fullName && sameVersion;
            if (!exact && !fullName && !(sameVersion && (samePublisher || hits > 0))) continue;
            var rank = (exact ? 1 : 0, hits, name.Count);
            if (best is not null && rank.CompareTo(bestRank) <= 0) continue;
            (best, bestRank) = (new(e, exact, fullName ? "name" : "version"), rank);
        }
        return best;
    }
}

/// <summary>.msi: ProductCode from the file's Property table + MsiQueryProductState; UpgradeCode + MsiEnumRelatedProducts for another version.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows8.0")]
public static unsafe class Msi
{
    public static Dictionary<string, string>? Properties(string path)
    {
        MSIHANDLE db = default, view = default;
        fixed (char* p = path)
            if (PInvoke.MsiOpenDatabase(p, default, &db) != 0) return null; // szPersist null = MSIDBOPEN_READONLY
        try
        {
            fixed (char* q = "SELECT `Property`, `Value` FROM `Property`")
                if (PInvoke.MsiDatabaseOpenView(db, q, &view) != 0 || PInvoke.MsiViewExecute(view, default) != 0) return null;
            var r = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (MSIHANDLE rec = default; PInvoke.MsiViewFetch(view, &rec) == 0; PInvoke.MsiCloseHandle(rec))
                r[Field(rec, 1)] = Field(rec, 2);
            return r;
        }
        finally
        {
            if (view != default) PInvoke.MsiCloseHandle(view);
            PInvoke.MsiCloseHandle(db);
        }
    }

    static string Field(MSIHANDLE rec, uint i)
    {
        uint n = 0;
        char empty = '\0';
        PInvoke.MsiRecordGetString(rec, i, new PWSTR(&empty), &n); // ERROR_MORE_DATA, n = length without the null
        var buf = new char[n + 1];
        n++;
        fixed (char* b = buf) return PInvoke.MsiRecordGetString(rec, i, new PWSTR(b), &n) == 0 ? new string(b, 0, (int)n) : "";
    }

    public static bool IsInstalled(string productCode)
    {
        fixed (char* p = productCode) return PInvoke.MsiQueryProductState(p) == INSTALLSTATE.INSTALLSTATE_DEFAULT;
    }

    /// <summary>ProductCode of an installed product of the same family (UpgradeCode), or null.</summary>
    public static string? Related(string upgradeCode)
    {
        var buf = stackalloc char[39];
        fixed (char* u = upgradeCode) return PInvoke.MsiEnumRelatedProducts(u, 0, 0, new PWSTR(buf)) == 0 ? new string(buf) : null;
    }
}

public static class Installers
{
    /// <summary>The offer for one .msi or .exe, or null (no match: never offered).</summary>
    public static Suggestion? Check(Item i, IReadOnlyList<UninstallEntry> entries)
    {
        if (i.IsDir) return null;
        var ext = Path.GetExtension(i.Path).ToLowerInvariant();
        if (ext == ".msi" && OperatingSystem.IsWindowsVersionAtLeast(8)) // always true on .NET 10; tells CA1416 the msi.dll calls are fine
        {
            if (Msi.Properties(i.Path) is not { } p) return null;
            var name = p.GetValueOrDefault("ProductName") ?? i.Name;
            if (p.GetValueOrDefault("ProductCode") is { } code && Msi.IsInstalled(code))
                return new("installer", "exact", name, p.GetValueOrDefault("ProductVersion"), Why: "msi");
            if (p.GetValueOrDefault("UpgradeCode") is { } up && Msi.Related(up) is { } other)
            {
                var e = entries.FirstOrDefault(e => e.SubKey.Equals(other, StringComparison.OrdinalIgnoreCase));
                return new("installer", "other", e?.DisplayName ?? name, e?.DisplayVersion, Why: "msi");
            }
            return null;
        }
        if (ext != ".exe") return null;
        return ExeMatcher.Match(i.Name, ExeInfo.Read(i.Path), entries) is { } m
            ? new("installer", m.Exact ? "exact" : "likely", m.Entry.DisplayName!, m.Entry.DisplayVersion, Why: m.Why)
            : null;
    }
}

/// <summary>.zip only (central directory, nothing extracted). Covered entries = same name and size in a folder next to the archive.</summary>
public static class ZipCoverage
{
    public const int Threshold = 98;
    public const int MaxEntries = 20_000; // ponytail: bigger archives aren't checked; raise if real archives hit it

    /// <summary>Best coverage in %, floored (99.9 is not 100), and where: "" = right next to the archive, else a folder name.</summary>
    public static (int Percent, string Where)? Check(string zip)
    {
        List<ZipArchiveEntry> files;
        ZipArchive z;
        try { z = ZipFile.OpenRead(zip); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { return null; }
        using (z)
        {
            files = z.Entries.Where(e => !e.FullName.EndsWith('/') && !e.FullName.EndsWith('\\') && !e.FullName.Contains("..")).ToList();
            if (files.Count == 0 || files.Count > MaxEntries) return null;
            var dir = Path.GetDirectoryName(zip)!;
            var stem = Path.GetFileNameWithoutExtension(zip);
            var roots = files.Select(e => e.FullName.Replace('\\', '/').Split('/')[0]).Distinct().ToList();
            var root = roots.Count == 1 && files.All(e => e.FullName.Replace('\\', '/').Contains('/')) ? roots[0] + "/" : null;

            // <name>\ as is (Explorer "Extract all"), right here (7-Zip "Extract here"), <name>\ with the single root folder dropped
            (string Base, string? Strip, string Where)[] candidates =
            [
                (Path.Combine(dir, stem), null, stem),
                (dir, null, root is null ? "" : root.TrimEnd('/')),
                (Path.Combine(dir, stem), root, stem),
            ];
            (int, string)? best = null;
            foreach (var (b, strip, where) in candidates)
            {
                if (!Directory.Exists(b)) continue;
                var hit = files.Count(e =>
                {
                    var rel = e.FullName.Replace('\\', '/');
                    if (strip is not null) rel = rel[strip.Length..];
                    var f = new FileInfo(Path.Combine(b, rel.Replace('/', '\\')));
                    return f.Exists && f.Length == e.Length;
                });
                var pct = (int)(hit * 100L / files.Count);
                if (best is null || pct > best.Value.Item1) best = (pct, where);
            }
            return best;
        }
    }
}
